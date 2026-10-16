using System.Text.Json;

using CareFlow.Audit.Domain.Chain;
using CareFlow.Audit.IntegrationTests.Infrastructure;
using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts.Rights;
using CareFlow.Testing;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Rights;

/// <summary>
/// A auditoria respondendo a pedidos do titular (ADR-0012): a trilha do pseudônimo vai cifrada para o pedido; na
/// eliminação nada sai da cadeia, o comprovante diz quanto ficou e a verificação continua íntegra.
/// </summary>
public sealed class SubjectRequestTests(AuditApiFixture api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Guid _clinic = Guid.NewGuid();
    private readonly Guid _patient = Guid.NewGuid();

    private async Task SeedTrailAsync(Guid clinic, Guid patientRef, int entries)
    {
        for (var i = 0; i < entries; i++)
        {
            var auditEvent = AuditEvent.Create(
                clinic, Guid.CreateVersion7(), DateTimeOffset.UtcNow, "patient-vault", "usuario-sintetico", "patient.read", "patient", patientRef).Value;
            await api.AppendAsync(auditEvent, TestContext.Current.CancellationToken);
        }
    }

    private async Task<SubjectRequestAnswered> AnswerAsync(Guid clinic, Guid requestId)
    {
        var rows = await api.OutboxAsync(clinic);
        return rows.AnswerTo(requestId).Deserialize<SubjectRequestAnswered>(Json)!;
    }

    [Fact]
    public async Task Export_SealsThePatientTrail_AndRecordsTheExportInTheChain()
    {
        // Arrange
        await SeedTrailAsync(_clinic, _patient, 2);
        await SeedTrailAsync(_clinic, Guid.NewGuid(), 1);
        using var requestKey = SealedBox.CreateKeyPair();
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), _patient, SubjectRequestKinds.Access, SealedBox.ExportPublicKey(requestKey), RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        var outcome = await api.DeliverAsync(_clinic, opened, Guid.CreateVersion7());

        // Assert
        Assert.Equal(ProcessOutcome.Processed, outcome);
        var answer = await AnswerAsync(_clinic, opened.RequestId);
        Assert.Equal(SubjectRequestOutcomes.Exported, answer.Outcome);
        Assert.Equal(SubjectRequestServices.Audit, answer.Service);

        var json = SealedBox.Open(requestKey, answer.SealedSection!, SubjectRequestSealing.SectionContext(opened.RequestId, SubjectRequestServices.Audit));
        using var section = JsonDocument.Parse(json);
        var entries = section.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("patient.read", e.GetProperty("action").GetString()));
        Assert.All(entries, e => Assert.Equal(64, e.GetProperty("hash").GetString()!.Length));

        var trail = await api.CreateClient(_clinic, Read).ListOkAsync($"?resourceId={_patient}");
        Assert.Contains(trail.Items, e => e.Action == "audit.exported" && e.Actor == "careflow-rights");
        Assert.True((await api.CreateClient(_clinic, Read).VerifyOkAsync()).Intact);
    }

    [Fact]
    public async Task Export_WithoutRequestKey_IsRejected()
    {
        // Arrange
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), _patient, SubjectRequestKinds.Portability, null, RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        var outcome = await api.DeliverAsync(_clinic, opened, Guid.CreateVersion7());

        // Assert
        Assert.Equal(ProcessOutcome.Invalid, outcome);
        Assert.DoesNotContain(await api.OutboxAsync(_clinic), row => row.Type == SubjectRequestAnswered.EventType);
    }

    [Fact]
    public async Task Erasure_KeepsTheTrail_ReportsIt_AndTheChainStaysIntact()
    {
        // Arrange
        await SeedTrailAsync(_clinic, _patient, 3);
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), _patient, SubjectRequestKinds.Erasure, null, RetentionPolicy.Version, DateTimeOffset.UtcNow);
        var messageId = Guid.CreateVersion7();

        // Act
        var first = await api.DeliverAsync(_clinic, opened, messageId);
        var again = await api.DeliverAsync(_clinic, opened, messageId);

        // Assert
        Assert.Equal(ProcessOutcome.Processed, first);
        Assert.Equal(ProcessOutcome.Duplicate, again);
        var answer = await AnswerAsync(_clinic, opened.RequestId);
        Assert.Equal(SubjectRequestOutcomes.Erased, answer.Outcome);
        var line = Assert.Single(answer.Erasure);
        Assert.Equal(new ErasureLine(RetentionPolicy.Items.AuditTrail, RetentionActions.Retained, 3), line);

        var trail = await api.CreateClient(_clinic, Read).ListOkAsync($"?resourceId={_patient}");
        Assert.Equal(4, trail.Items.Length);
        Assert.Single(trail.Items, e => e.Action == "audit.retained-after-erasure");
        Assert.True((await api.CreateClient(_clinic, Read).VerifyOkAsync()).Intact);
    }

    [Fact]
    public async Task Export_OnlySeesTheTrailOfTheRequestingClinic()
    {
        // Arrange: o mesmo pseudônimo com registros em outra clínica.
        var otherClinic = Guid.NewGuid();
        await SeedTrailAsync(otherClinic, _patient, 2);
        using var requestKey = SealedBox.CreateKeyPair();
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), _patient, SubjectRequestKinds.Access, SealedBox.ExportPublicKey(requestKey), RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        var outcome = await api.DeliverAsync(_clinic, opened, Guid.CreateVersion7());

        // Assert
        Assert.Equal(ProcessOutcome.Processed, outcome);
        var answer = await AnswerAsync(_clinic, opened.RequestId);
        var json = SealedBox.Open(requestKey, answer.SealedSection!, SubjectRequestSealing.SectionContext(opened.RequestId, SubjectRequestServices.Audit));
        using var section = JsonDocument.Parse(json);
        Assert.Equal(0, section.RootElement.GetProperty("entries").GetArrayLength());
        Assert.DoesNotContain(await api.OutboxAsync(otherClinic), row => row.Type == SubjectRequestAnswered.EventType);
        Assert.Equal(2, (await api.CreateClient(otherClinic, Read).ListOkAsync($"?resourceId={_patient}")).Items.Length);
    }
}
