using System.Net;

using CareFlow.Contracts.Rights;
using CareFlow.Rights.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.Rights.IntegrationTests.Infrastructure.RightsClient;

namespace CareFlow.Rights.IntegrationTests.Requests;

/// <summary>Recebimento, confirmação de identidade e eliminação com comprovante.</summary>
public sealed class IdentityAndErasureTests(RightsApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    private HttpClient Client => api.CreateClient(_clinic, Read, Write);

    [Fact]
    public async Task Open_ReceivesWithDeadline_AndSendsTheCodeSealedForNotificationsOnly()
    {
        // Arrange
        var patient = Guid.NewGuid();

        // Act
        var request = await Client.OpenOkAsync(patient, "access");

        // Assert
        Assert.Equal("received", request.Status);
        Assert.Equal(request.ReceivedAt.AddDays(15), request.DueAt);
        Assert.False(request.Overdue);
        Assert.Empty(request.Services);

        var code = await api.CodeForAsync(_clinic, request.Id);
        Assert.Matches("^[0-9]{6}$", code);
        var outbox = await api.OutboxAsync(_clinic);
        Assert.DoesNotContain(outbox, row => row.Payload.GetRawText().Contains($"\"{code}\"", StringComparison.Ordinal));
        Assert.DoesNotContain(outbox, row => row.Type == SubjectRequestOpened.EventType);
        var hash = await AdminSql.ScalarAsync<byte[]>(api.AdminConnectionString, "SELECT code_hash FROM subject_requests WHERE id = @id", ("id", request.Id));
        Assert.Equal(32, hash!.Length);
    }

    [Fact]
    public async Task Open_SameKindTwiceWhileOpen_IsAConflict_ButAnotherKindIsNot()
    {
        // Arrange
        var patient = Guid.NewGuid();
        await Client.OpenOkAsync(patient, "access");

        // Act
        using var again = await Client.OpenAsync(patient, "access");
        using var other = await Client.OpenAsync(patient, "erasure");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("Direitos.PedidoEmAberto", (await again.ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("deletion")]
    [InlineData("ACCESS")]
    public async Task Open_UnknownKind_Is400(string? kind)
    {
        using var response = await Client.OpenAsync(Guid.NewGuid(), kind!);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_WrongCodeFiveTimes_DeniesTheRequest_AndTheRightCodeNoLongerWorks()
    {
        // Arrange
        var request = await Client.OpenOkAsync(Guid.NewGuid(), "access");
        var code = await api.CodeForAsync(_clinic, request.Id);
        var wrong = code == "000000" ? "000001" : "000000";

        // Act
        var remaining = new List<int?>();
        for (var i = 0; i < 4; i++)
        {
            using var attempt = await Client.ConfirmAsync(request.Id, wrong);
            Assert.Equal(HttpStatusCode.BadRequest, attempt.StatusCode);
            remaining.Add((await attempt.ProblemAsync()).RemainingAttempts);
        }

        using var last = await Client.ConfirmAsync(request.Id, wrong);
        using var right = await Client.ConfirmAsync(request.Id, code);

        // Assert
        Assert.Equal([4, 3, 2, 1], remaining);
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Equal("Direitos.TentativasEsgotadas", (await last.ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Conflict, right.StatusCode);
        var denied = await Client.GetOkAsync(request.Id);
        Assert.Equal("denied", denied.Status);
        Assert.Equal("identity-not-confirmed", denied.DenialReason);
        Assert.DoesNotContain(await api.OutboxAsync(_clinic), row => row.Type == SubjectRequestOpened.EventType);
    }

    [Fact]
    public async Task Confirm_MalformedCode_DoesNotSpendAnAttempt()
    {
        var request = await Client.OpenOkAsync(Guid.NewGuid(), "access");

        using var response = await Client.ConfirmAsync(request.Id, "12ab");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await AdminSql.ScalarAsync<int>(api.AdminConnectionString, "SELECT code_attempts FROM subject_requests WHERE id = @id", ("id", request.Id)));
    }

    [Fact]
    public async Task Confirm_ExpiredCode_DeniesTheRequest()
    {
        // Arrange
        var request = await Client.OpenOkAsync(Guid.NewGuid(), "erasure");
        var code = await api.CodeForAsync(_clinic, request.Id);
        await AdminSql.ExecuteAsync(api.AdminConnectionString,
            "UPDATE subject_requests SET code_expires_at = now() - interval '1 second' WHERE id = @id", ("id", request.Id));

        // Act
        using var response = await Client.ConfirmAsync(request.Id, code);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Direitos.CodigoExpirado", (await response.ProblemAsync()).Code);
        Assert.Equal("identity-not-confirmed", (await Client.GetOkAsync(request.Id)).DenialReason);
    }

    [Theory]
    [InlineData(IdentityVerificationDispatched.NoContact, "no-contact")]
    [InlineData(IdentityVerificationDispatched.Failed, "code-not-delivered")]
    public async Task Dispatch_WithoutDelivery_DeniesWithTheReason(string outcome, string reason)
    {
        var request = await Client.OpenOkAsync(Guid.NewGuid(), "access");

        await api.DeliverAsync(_clinic, new IdentityVerificationDispatched(request.Id, outcome, null, DateTimeOffset.UtcNow));

        var denied = await Client.GetOkAsync(request.Id);
        Assert.Equal("denied", denied.Status);
        Assert.Equal(reason, denied.DenialReason);
    }

    [Fact]
    public async Task Dispatch_Sent_RecordsTheChannel_AndTheRequestKeepsWaiting()
    {
        var request = await Client.OpenOkAsync(Guid.NewGuid(), "access");

        await api.DeliverAsync(_clinic, new IdentityVerificationDispatched(request.Id, IdentityVerificationDispatched.Sent, "email", DateTimeOffset.UtcNow));

        var current = await Client.GetOkAsync(request.Id);
        Assert.Equal("received", current.Status);
        Assert.Equal("email", current.CodeChannel);
        Assert.NotNull(current.CodeSentAt);
    }

    [Fact]
    public async Task Erasure_CompletesWithAReceipt_FromThePolicyInForce()
    {
        // Arrange
        var scenario = new RequestScenario(api, _clinic);
        var request = await scenario.OpenAndConfirmAsync("erasure");
        var opened = await scenario.OpenedAsync(request.Id);

        // Act
        await scenario.AnswerErasureAsync(request.Id);

        // Assert
        Assert.Null(opened.RecipientPublicKey);
        Assert.Equal(RetentionPolicy.Version, opened.PolicyVersion);
        var done = await scenario.Client.GetOkAsync(request.Id);
        Assert.Equal("completed", done.Status);
        Assert.Null(done.Package);
        var receipt = done.Receipt!;
        Assert.Equal(RetentionPolicy.Version, receipt.PolicyVersion);
        Assert.Equal(RetentionPolicy.Rules.Count, receipt.Lines.Count);
        Assert.All(receipt.Lines, line => Assert.Equal(1, line.Records));
        var past = receipt.Lines.Single(l => l.Item == RetentionPolicy.Items.SchedulingPastAppointments);
        Assert.Equal(receipt.ErasedAt.AddDays(RetentionPolicy.FiveYears), past.RetainedUntil);
        Assert.Null(receipt.Lines.Single(l => l.Item == RetentionPolicy.Items.VaultPersonalData).RetainedUntil);
    }

    [Fact]
    public async Task Erasure_LineOutsideTheServicePolicy_IsRejected()
    {
        var scenario = new RequestScenario(api, _clinic);
        var request = await scenario.OpenAndConfirmAsync("erasure");

        var outcome = await api.DeliverAsync(_clinic, new SubjectRequestAnswered(
            request.Id, SubjectRequestServices.Triage, SubjectRequestOutcomes.Erased, null,
            [new ErasureLine(RetentionPolicy.Items.AuditTrail, RetentionActions.Erased, 3)], DateTimeOffset.UtcNow));

        Assert.Equal(BuildingBlocks.Messaging.Inbox.ProcessOutcome.Invalid, outcome);
    }

    [Fact]
    public async Task Replay_OnlyForCompletedErasures_AndOnlyWithTheAdminScope()
    {
        // Arrange
        var scenario = new RequestScenario(api, _clinic);
        var erasure = await scenario.OpenAndConfirmAsync("erasure");
        await scenario.AnswerErasureAsync(erasure.Id);
        var access = await Client.OpenOkAsync(Guid.NewGuid(), "access");
        var admin = api.CreateClient(_clinic, Admin);

        // Act
        using var withoutScope = await Client.ReplayAsync(erasure.Id);
        using var replayed = await admin.ReplayAsync(erasure.Id);
        using var notErasure = await admin.ReplayAsync(access.Id);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, withoutScope.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, notErasure.StatusCode);
        var outbox = await api.OutboxAsync(_clinic);
        var reapplied = Assert.Single(outbox, row => row.Type == ErasureReapplied.EventType);
        Assert.Equal(scenario.PatientRef, reapplied.Payload.GetProperty("patientRef").GetGuid());
        Assert.Contains(outbox, row => row.AuditAction == "subject-request.erasure-reapplied");
    }
}
