using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.Rights.IntegrationTests.Infrastructure.RightsClient;

namespace CareFlow.Rights.IntegrationTests.Requests;

/// <summary>Acesso e portabilidade do começo ao fim: código, respostas dos serviços, pacote cifrado e link de uso único.</summary>
public sealed class ExportFlowTests(RightsApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    [Fact]
    public async Task Export_CompletesOnlyWhenEveryServiceAnswered_AndThePackageDownloadsOnce()
    {
        // Arrange
        var scenario = new RequestScenario(api, _clinic);
        var request = await scenario.OpenAndConfirmAsync("portability");
        var opened = await scenario.OpenedAsync(request.Id);
        Assert.Equal(SubjectRequestKinds.Portability, opened.Kind);
        Assert.False(string.IsNullOrEmpty(opened.RecipientPublicKey));

        // Act: cinco respostas não concluem; a sexta sim.
        await scenario.AnswerExportAsync(opened, SubjectRequestServices.All.Take(5));
        var partial = await scenario.Client.GetOkAsync(request.Id);
        await scenario.AnswerExportAsync(opened, SubjectRequestServices.All.Skip(5));
        var done = await scenario.Client.GetOkAsync(request.Id);

        // Assert
        Assert.Equal("in-progress", partial.Status);
        Assert.Equal(5, partial.Services.Count(s => s.Outcome == SubjectRequestOutcomes.Exported));
        Assert.Equal("completed", done.Status);
        Assert.All(done.Services, s => Assert.Equal(SubjectRequestOutcomes.Exported, s.Outcome));
        Assert.True(done.Package!.Available);
        Assert.Equal(done.Package.CreatedAt.AddDays(7), done.Package.ExpiresAt);

        // A chave do pedido e as seções cifradas saem do banco; o pacote fica cifrado.
        Assert.Null(await AdminSql.ScalarAsync<byte[]?>(api.AdminConnectionString, "SELECT wrapped_recipient_key FROM subject_requests WHERE id = @id", ("id", request.Id)));
        Assert.Equal(0L, await AdminSql.ScalarAsync<long>(api.AdminConnectionString, "SELECT count(*) FROM subject_request_answers WHERE request_id = @id AND sealed_section IS NOT NULL", ("id", request.Id)));
        var stored = await AdminSql.ScalarAsync<byte[]>(api.AdminConnectionString, "SELECT content FROM subject_request_packages WHERE request_id = @id", ("id", request.Id));
        Assert.DoesNotContain(scenario.FullName, Encoding.UTF8.GetString(stored!), StringComparison.Ordinal);

        // Link: 15 minutos, uso único, sem conta.
        var link = await scenario.Client.CreateLinkOkAsync(request.Id);
        Assert.InRange(link.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        using var download = await api.CreateAnonymousClient().DownloadAsync(link.Token);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", download.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);

        await using var zipStream = await download.Content.ReadAsStreamAsync(Ct);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        Assert.Equal(["dados.json", "fhir-bundle.json", "leia-me.html"], zip.Entries.Select(e => e.FullName).Order().ToArray());
        using (var data = await JsonDocument.ParseAsync(zip.GetEntry("dados.json")!.Open(), cancellationToken: Ct))
        {
            var sections = data.RootElement.GetProperty("secoes");
            Assert.Equal(scenario.FullName, sections.GetProperty(SubjectRequestServices.Vault).GetProperty("fullName").GetString());
            Assert.Equal(SubjectRequestServices.All.Count, sections.EnumerateObject().Count());
        }

        using (var fhir = await JsonDocument.ParseAsync(zip.GetEntry("fhir-bundle.json")!.Open(), cancellationToken: Ct))
        {
            var types = fhir.RootElement.GetProperty("entry").EnumerateArray()
                .Select(e => e.GetProperty("resource").GetProperty("resourceType").GetString()).ToList();
            Assert.Equal(["Patient", "Appointment", "Appointment", "Consent"], types);
        }

        using var again = await api.CreateAnonymousClient().DownloadAsync(link.Token);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var outbox = await api.OutboxAsync(_clinic);
        var actions = outbox.Select(r => r.AuditAction).Where(a => a is not null).ToList();
        Assert.Equal(
            ["subject-request.received", "subject-request.identity-confirmed", "subject-request.completed", "subject-request.download-link-created", "subject-request.package-downloaded"],
            actions);
    }

    [Fact]
    public async Task Export_SectionSealedForAnotherRequest_IsRejected_AndTheRequestDoesNotComplete()
    {
        // Arrange: a seção de outro pedido (mesma chave pública, outro contexto) não pode ser reaproveitada.
        var scenario = new RequestScenario(api, _clinic);
        var request = await scenario.OpenAndConfirmAsync("access");
        var opened = await scenario.OpenedAsync(request.Id);
        await scenario.AnswerExportAsync(opened, SubjectRequestServices.All.Take(5));
        var forged = SealedBox.Seal(
            opened.RecipientPublicKey!, "{}"u8, SubjectRequestSealing.SectionContext(Guid.NewGuid(), SubjectRequestServices.Audit));

        // Act
        var outcome = await api.DeliverAsync(_clinic, new SubjectRequestAnswered(
            request.Id, SubjectRequestServices.Audit, SubjectRequestOutcomes.Exported, forged, [], DateTimeOffset.UtcNow));

        // Assert
        Assert.Equal(ProcessOutcome.Invalid, outcome);
        Assert.Equal("in-progress", (await scenario.Client.GetOkAsync(request.Id)).Status);
    }

    [Fact]
    public async Task Export_VaultWithoutThePatient_DeniesTheRequest_AndLaterAnswersAreIgnored()
    {
        // Arrange
        var scenario = new RequestScenario(api, _clinic);
        var request = await scenario.OpenAndConfirmAsync("access");
        var opened = await scenario.OpenedAsync(request.Id);

        // Act
        await api.DeliverAsync(_clinic, new SubjectRequestAnswered(
            request.Id, SubjectRequestServices.Vault, SubjectRequestOutcomes.SubjectNotFound, null, [], DateTimeOffset.UtcNow));
        await scenario.AnswerExportAsync(opened, SubjectRequestServices.All.Skip(1));

        // Assert
        var denied = await scenario.Client.GetOkAsync(request.Id);
        Assert.Equal("denied", denied.Status);
        Assert.Equal("subject-not-found", denied.DenialReason);
        Assert.Null(denied.Package);
        Assert.Null(await AdminSql.ScalarAsync<byte[]?>(api.AdminConnectionString, "SELECT wrapped_recipient_key FROM subject_requests WHERE id = @id", ("id", request.Id)));
        using var link = await scenario.Client.CreateLinkAsync(request.Id);
        Assert.Equal(HttpStatusCode.Conflict, link.StatusCode);
    }

    [Fact]
    public async Task Answers_RepeatedOrMismatched_AreHandledWithoutDoubleCounting()
    {
        // Arrange
        var scenario = new RequestScenario(api, _clinic);
        var request = await scenario.OpenAndConfirmAsync("access");
        var opened = await scenario.OpenedAsync(request.Id);
        await scenario.AnswerExportAsync(opened, [SubjectRequestServices.Vault]);

        // Act: a mesma resposta de novo (outra mensagem) e uma resposta de eliminação num pedido de acesso.
        await scenario.AnswerExportAsync(opened, [SubjectRequestServices.Vault]);
        var mismatched = await api.DeliverAsync(_clinic, new SubjectRequestAnswered(
            request.Id, SubjectRequestServices.Audit, SubjectRequestOutcomes.Erased, null, [], DateTimeOffset.UtcNow));
        var unknown = await api.DeliverAsync(_clinic, new SubjectRequestAnswered(
            request.Id, "billing", SubjectRequestOutcomes.Exported, "x", [], DateTimeOffset.UtcNow));

        // Assert
        Assert.Equal(ProcessOutcome.Invalid, mismatched);
        Assert.Equal(ProcessOutcome.Invalid, unknown);
        var current = await scenario.Client.GetOkAsync(request.Id);
        Assert.Single(current.Services, s => s.Outcome is not null);
    }

    [Fact]
    public async Task Sweep_DestroysExpiredPackages_AndTheirLinks()
    {
        // Arrange
        var scenario = new RequestScenario(api, _clinic);
        var request = await scenario.OpenAndConfirmAsync("access");
        await scenario.AnswerExportAsync(await scenario.OpenedAsync(request.Id));
        var link = await scenario.Client.CreateLinkOkAsync(request.Id);
        await AdminSql.ExecuteAsync(api.AdminConnectionString,
            "UPDATE subject_request_packages SET expires_at = now() - interval '1 minute' WHERE request_id = @id; " +
            "UPDATE package_expirations SET expires_at = now() - interval '1 minute' WHERE request_id = @id",
            ("id", request.Id));

        // Act
        var swept = await api.SweepAsync();

        // Assert
        Assert.True(swept >= 1);
        var current = await scenario.Client.GetOkAsync(request.Id);
        Assert.False(current.Package!.Available);
        Assert.NotNull(current.Package.DestroyedAt);
        Assert.Equal(0L, await AdminSql.ScalarAsync<long>(api.AdminConnectionString,
            "SELECT count(*) FROM subject_request_packages WHERE request_id = @id AND (content IS NOT NULL OR wrapped_key IS NOT NULL)", ("id", request.Id)));
        Assert.Equal(0L, await AdminSql.ScalarAsync<long>(api.AdminConnectionString, "SELECT count(*) FROM download_links WHERE request_id = @id", ("id", request.Id)));
        using var download = await api.CreateAnonymousClient().DownloadAsync(link.Token);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        using var newLink = await scenario.Client.CreateLinkAsync(request.Id);
        Assert.Equal(HttpStatusCode.Conflict, newLink.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-um-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Download_UnknownOrMalformedToken_Is404(string token)
    {
        // Act
        using var response = await api.CreateAnonymousClient().DownloadAsync(token);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Direitos.LinkIndisponivel", (await response.ProblemAsync()).Code);
    }
}
