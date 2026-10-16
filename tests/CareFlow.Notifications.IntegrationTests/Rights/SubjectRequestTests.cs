using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts.Appointments;
using CareFlow.Contracts.Rights;
using CareFlow.Notifications.Api.Features.Rights;
using CareFlow.Notifications.IntegrationTests.Infrastructure;
using CareFlow.Notifications.IntegrationTests.Notifications;
using CareFlow.Testing;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Rights;

/// <summary>
/// O Notifications nos pedidos do titular (ADR-0012): envio do código de confirmação, caixa simulada da
/// demonstração, exportação e eliminação dos avisos.
/// </summary>
public sealed class SubjectRequestTests(NotificationsApiFixture api) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await api.ResetChannelsAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IdentityVerificationRequested Verification(Guid patientRef, string code = "482913", Guid? requestId = null, TimeSpan? validity = null)
    {
        var id = requestId ?? Guid.NewGuid();
        var sealedCode = SealedBox.Seal(
            SealedBox.ExportPublicKey(api.VerificationKey), Encoding.ASCII.GetBytes(code), SubjectRequestSealing.VerificationCodeContext(id));
        return new IdentityVerificationRequested(id, patientRef, sealedCode, DateTimeOffset.UtcNow + (validity ?? TimeSpan.FromMinutes(10)));
    }

    private async Task<JsonElement> DispatchedAsync(Guid tenant, Guid requestId) =>
        (await OutboxRows.ReadAsync(api.AdminConnectionString, tenant, Ct))
            .Single(row => row.Type == IdentityVerificationDispatched.EventType && row.Payload.GetProperty("requestId").GetGuid() == requestId)
            .Payload;

    [Fact]
    public async Task Verification_SendsTheCodeToTheRegisteredContact_AndOnlyTheClinicSeesItInTheSimulatedInbox()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var request = Verification(scenario.Patient);

        // Act
        var outcome = await api.DeliverAsync(scenario.Tenant, request);

        // Assert
        Assert.Equal(ProcessOutcome.Processed, outcome);
        var dispatched = await DispatchedAsync(scenario.Tenant, request.RequestId);
        Assert.Equal(IdentityVerificationDispatched.Sent, dispatched.GetProperty("outcome").GetString());
        Assert.Equal("whatsapp", dispatched.GetProperty("channel").GetString());

        var message = Assert.Single(api.Ledger.Messages, m => m.Text.Contains("482913", StringComparison.Ordinal));
        Assert.Equal(api.Ledger.Fingerprint(scenario.Contact.Phone!), message.RecipientFingerprint);

        var inbox = await api.CreateClient(scenario.Tenant, "notifications.demo-inbox")
            .GetFromJsonAsync<SimulatedInboxMessage[]>($"/v1/demo/verification-messages/{request.RequestId}", Json, Ct);
        Assert.Contains("482913", Assert.Single(inbox!).Text, StringComparison.Ordinal);

        var otherClinic = await api.CreateClient(Guid.NewGuid(), "notifications.demo-inbox")
            .GetFromJsonAsync<SimulatedInboxMessage[]>($"/v1/demo/verification-messages/{request.RequestId}", Json, Ct);
        Assert.Empty(otherClinic!);

        var withoutScope = await api.CreateClient(scenario.Tenant, Read).GetAsync($"/v1/demo/verification-messages/{request.RequestId}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, withoutScope.StatusCode);

        Assert.Contains("identity-verification.sent.whatsapp", await scenario.AuditActionsAsync());
        Assert.DoesNotContain(api.Logs.Entries, entry => entry.OwnContent.Contains("482913", StringComparison.Ordinal));
        Assert.DoesNotContain(
            await OutboxRows.ReadAsync(api.AdminConnectionString, scenario.Tenant, Ct),
            row => row.Payload.GetRawText().Contains("482913", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verification_WithoutContact_AnswersNoContact()
    {
        // Arrange: paciente sem contato no cofre (ou eliminado).
        var tenant = Guid.NewGuid();
        var request = Verification(Guid.NewGuid());

        // Act
        await api.DeliverAsync(tenant, request);

        // Assert
        var dispatched = await DispatchedAsync(tenant, request.RequestId);
        Assert.Equal(IdentityVerificationDispatched.NoContact, dispatched.GetProperty("outcome").GetString());
        Assert.DoesNotContain(api.Ledger.Messages, m => m.IdempotencyKey.Contains(request.RequestId.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verification_ExpiredBeforeSending_IsNotSent()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var request = Verification(scenario.Patient, validity: TimeSpan.FromSeconds(-1));

        // Act
        await api.DeliverAsync(scenario.Tenant, request);

        // Assert
        Assert.Equal(IdentityVerificationDispatched.Failed, (await DispatchedAsync(scenario.Tenant, request.RequestId)).GetProperty("outcome").GetString());
        Assert.Equal(0, api.Contacts.ReadsOf(scenario.Tenant, scenario.Patient));
    }

    [Fact]
    public async Task Verification_SealedForAnotherRequest_GoesToDeadLetters()
    {
        // Arrange: envelope de um pedido, evento de outro.
        var scenario = await Scenario.ReadyAsync(api);
        var original = Verification(scenario.Patient);
        var tampered = original with { RequestId = Guid.NewGuid() };

        // Act
        var outcome = await api.DeliverAsync(scenario.Tenant, tampered);

        // Assert
        Assert.Equal(ProcessOutcome.Invalid, outcome);
    }

    [Fact]
    public async Task Erasure_CancelsPendingNotices_ErasesProviderReferences_AndKeepsTheRecord()
    {
        // Arrange: confirmação enviada e lembrete pendente.
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();
        await api.DispatchAsync();
        var opened = new SubjectRequestOpened(Guid.NewGuid(), scenario.Patient, SubjectRequestKinds.Erasure, null, RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        await api.DeliverAsync(scenario.Tenant, opened);
        await api.DeliverAsync(scenario.Tenant, Cancelled(booked, AppointmentCancellationReasons.SubjectErasure));
        await api.DispatchAsync();

        // Assert
        var notices = await scenario.NotificationsAsync(booked.AppointmentId);
        Assert.Equal("Sent", notices.Single(n => n.Kind == "Confirmation").Status);
        Assert.Equal("Superseded", notices.Single(n => n.Kind == "Reminder").Status);
        Assert.DoesNotContain(notices, n => n.Kind == "Cancellation");
        Assert.Equal(0L, await AdminSql.ScalarAsync<long>(
            api.AdminConnectionString, "SELECT count(*) FROM notifications WHERE patient_ref = @p AND provider_message_id IS NOT NULL", ("p", scenario.Patient)));

        var lines = (await OutboxRows.ReadAsync(api.AdminConnectionString, scenario.Tenant, Ct)).AnswerTo(opened.RequestId)
            .GetProperty("erasure").Deserialize<List<ErasureLine>>(Json)!;
        Assert.Contains(new ErasureLine(RetentionPolicy.Items.NotificationsPending, RetentionActions.Cancelled, 1), lines);
        Assert.Contains(new ErasureLine(RetentionPolicy.Items.NotificationsProviderReference, RetentionActions.Erased, 1), lines);
        Assert.Contains(new ErasureLine(RetentionPolicy.Items.NotificationsRecords, RetentionActions.Retained, 2), lines);
    }

    [Fact]
    public async Task Export_ListsNoticesWithoutTextOrContact()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();
        await api.DispatchAsync();
        using var key = SealedBox.CreateKeyPair();
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), scenario.Patient, SubjectRequestKinds.Access, SealedBox.ExportPublicKey(key), RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        await api.DeliverAsync(scenario.Tenant, opened);

        // Assert
        var answer = (await OutboxRows.ReadAsync(api.AdminConnectionString, scenario.Tenant, Ct)).AnswerTo(opened.RequestId);
        var json = Encoding.UTF8.GetString(SealedBox.Open(
            key, answer.GetProperty("sealedSection").GetString()!, SubjectRequestSealing.SectionContext(opened.RequestId, SubjectRequestServices.Notifications)));
        using var section = JsonDocument.Parse(json);
        var items = section.RootElement.GetProperty("notifications").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(booked.AppointmentId, item.GetProperty("appointmentId").GetGuid()));
        Assert.DoesNotContain(scenario.Contact.Phone!, json, StringComparison.Ordinal);
    }
}
