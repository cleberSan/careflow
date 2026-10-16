using System.Text;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.Contracts.Rights;
using CareFlow.Testing;
using CareFlow.Triage.IntegrationTests.Infrastructure;

using static CareFlow.Triage.IntegrationTests.Infrastructure.TriageClient;

namespace CareFlow.Triage.IntegrationTests.Rights;

/// <summary>A triagem nos pedidos do titular: exportação com o texto enviado ao modelo e eliminação dos textos (ADR-0012).</summary>
public sealed class SubjectRequestTests
{
    private const string ComplaintText = "Tosse seca há três dias, sem febre, cansaço leve.";

    private readonly TriageApiFixture _api;
    private readonly Guid _clinic = Guid.NewGuid();

    public SubjectRequestTests(TriageApiFixture api)
    {
        _api = api;
        api.Reset();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Export_CarriesComplaintTextSentToModelSuggestionAndDecision()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, ComplaintText);
        await _api.CreateClient(_clinic, Review).ReviewAsync(triage.Id, "confirm");
        using var key = SealedBox.CreateKeyPair();
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), patient, SubjectRequestKinds.Access, SealedBox.ExportPublicKey(key), RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        await _api.DeliverAsync(_clinic, opened);

        // Assert
        var answer = (await _api.OutboxAsync(_clinic)).AnswerTo(opened.RequestId);
        var json = Encoding.UTF8.GetString(SealedBox.Open(
            key, answer.GetProperty("sealedSection").GetString()!, SubjectRequestSealing.SectionContext(opened.RequestId, SubjectRequestServices.Triage)));
        using var section = JsonDocument.Parse(json);
        var item = Assert.Single(section.RootElement.GetProperty("triages").EnumerateArray());
        Assert.Equal(ComplaintText, item.GetProperty("complaint").GetString());
        Assert.Equal(triage.SentToModel, item.GetProperty("sentToModel").GetString());
        Assert.Equal(triage.PromptDigest, item.GetProperty("promptDigest").GetString());
        Assert.Equal("confirmed", item.GetProperty("decision").GetString());
        Assert.Equal(triage.SuggestedPriority, item.GetProperty("finalPriority").GetString());
    }

    [Fact]
    public async Task Erasure_RemovesTexts_KeepsDecisionAndDigest()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, ComplaintText);
        var opened = new SubjectRequestOpened(Guid.NewGuid(), patient, SubjectRequestKinds.Erasure, null, RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        await _api.DeliverAsync(_clinic, opened);
        await _api.DeliverAsync(_clinic, new ErasureReapplied(opened.RequestId, patient, RetentionPolicy.Version, DateTimeOffset.UtcNow));

        // Assert
        var after = await _api.CreateClient(_clinic, Read).GetOkAsync(triage.Id);
        Assert.Null(after.Complaint);
        Assert.Null(after.SentToModel);
        Assert.Null(after.Rationale);
        Assert.Equal(triage.PromptDigest, after.PromptDigest);
        Assert.Equal(triage.SuggestedPriority, after.SuggestedPriority);
        Assert.Null(await AdminSql.ScalarAsync<string>(_api.AdminConnectionString, "SELECT complaint FROM triage_cases WHERE id = @id", ("id", triage.Id)));

        var outbox = await _api.OutboxAsync(_clinic);
        var lines = outbox.AnswerTo(opened.RequestId).GetProperty("erasure").Deserialize<List<ErasureLine>>(Json)!;
        Assert.Contains(new ErasureLine(RetentionPolicy.Items.TriageText, RetentionActions.Erased, 1), lines);
        Assert.Contains(new ErasureLine(RetentionPolicy.Items.TriageDecision, RetentionActions.Retained, 1), lines);

        // A trilha guarda o resumo do que saiu, que continua igual ao da triagem.
        Assert.Contains(outbox, row => row.ContentDigest == triage.PromptDigest);
        Assert.Single(outbox, row => row.AuditAction == "triage.text-erased");

        // O profissional ainda pode decidir uma triagem pendente: a decisão é o que fica.
        var review = await _api.CreateClient(_clinic, Review).ReviewAsync(triage.Id, "confirm");
        Assert.True(review.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ErasureFromAnotherClinic_KeepsTheTexts()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, ComplaintText);

        // Act
        await _api.DeliverAsync(Guid.NewGuid(), new SubjectRequestOpened(Guid.NewGuid(), patient, SubjectRequestKinds.Erasure, null, RetentionPolicy.Version, DateTimeOffset.UtcNow));

        // Assert
        Assert.Equal(ComplaintText, (await _api.CreateClient(_clinic, Read).GetOkAsync(triage.Id)).Complaint);
    }
}
