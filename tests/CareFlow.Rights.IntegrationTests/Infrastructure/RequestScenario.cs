using System.Net;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.BuildingBlocks.Messaging.Rights;
using CareFlow.Contracts.Rights;
using CareFlow.Testing;

using static CareFlow.Rights.IntegrationTests.Infrastructure.RightsClient;

namespace CareFlow.Rights.IntegrationTests.Infrastructure;

/// <summary>
/// Um pedido do começo ao fim, com os seis serviços simulados: as seções são cifradas para a chave do pedido
/// exatamente como <see cref="SubjectRequestAnswers"/> faz em cada serviço. Dados 100% sintéticos.
/// </summary>
internal sealed class RequestScenario(RightsApiFixture api, Guid clinic)
{
    public Guid PatientRef { get; } = Guid.NewGuid();

    public string FullName { get; } = SyntheticPatients.FullName();

    public string Cpf { get; } = SyntheticPatients.Cpf();

    public string Email { get; } = SyntheticPatients.Email();

    public HttpClient Client => api.CreateClient(clinic, Read, Write);

    public async Task<RequestDto> OpenAndConfirmAsync(string kind)
    {
        var request = await Client.OpenOkAsync(PatientRef, kind);
        var code = await api.CodeForAsync(clinic, request.Id);
        using var response = await Client.ConfirmAsync(request.Id, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return request;
    }

    public async Task<SubjectRequestOpened> OpenedAsync(Guid requestId)
    {
        var rows = await api.OutboxAsync(clinic);
        return rows.Single(r => r.Type == SubjectRequestOpened.EventType && r.Payload.GetProperty("requestId").GetGuid() == requestId)
            .Payload.Deserialize<SubjectRequestOpened>(Json)!;
    }

    public object SectionFor(string service) => service switch
    {
        SubjectRequestServices.Vault => new VaultSection(
            PatientRef, FullName, Cpf, new DateOnly(1990, 5, 17), Email, SyntheticPatients.Phone(), DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-2)),
        SubjectRequestServices.Scheduling => new SchedulingSection(
        [
            new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-10).AddMinutes(30), "Booked", DateTimeOffset.UtcNow.AddDays(-20), null, null),
            new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(5).AddMinutes(30), "Cancelled", DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-1), "PatientRequest"),
        ]),
        SubjectRequestServices.Consent => new ConsentSection(
            [new("scheduling", "consent", "2026-10", "Granted", DateTimeOffset.UtcNow.AddDays(-30), null, 1)],
            [new("scheduling", "consent", "2026-10", "Granted", 1, DateTimeOffset.UtcNow.AddDays(-30))]),
        _ => new { items = new[] { new { kind = "synthetic", service } } },
    };

    public async Task AnswerExportAsync(SubjectRequestOpened opened, IEnumerable<string>? services = null)
    {
        foreach (var service in services ?? SubjectRequestServices.All)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(SectionFor(service), SubjectRequestAnswers.SectionJson);
            var sealedSection = SealedBox.Seal(opened.RecipientPublicKey!, json, SubjectRequestSealing.SectionContext(opened.RequestId, service));
            var outcome = await api.DeliverAsync(clinic, new SubjectRequestAnswered(opened.RequestId, service, SubjectRequestOutcomes.Exported, sealedSection, [], DateTimeOffset.UtcNow));
            Assert.Equal(ProcessOutcome.Processed, outcome);
        }
    }

    public async Task AnswerErasureAsync(Guid requestId, IEnumerable<string>? services = null)
    {
        foreach (var service in services ?? SubjectRequestServices.All)
        {
            var lines = RetentionPolicy.For(service).Select(rule => new ErasureLine(rule.Item, rule.Action, 1)).ToList();
            var outcome = await api.DeliverAsync(clinic, new SubjectRequestAnswered(requestId, service, SubjectRequestOutcomes.Erased, null, lines, DateTimeOffset.UtcNow));
            Assert.Equal(ProcessOutcome.Processed, outcome);
        }
    }
}
