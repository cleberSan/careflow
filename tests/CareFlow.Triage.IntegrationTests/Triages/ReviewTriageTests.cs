using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CareFlow.Triage.Api.Features.Triages;
using CareFlow.Triage.IntegrationTests.Infrastructure;

using static CareFlow.Triage.IntegrationTests.Infrastructure.TriageClient;

namespace CareFlow.Triage.IntegrationTests.Triages;

/// <summary>A sugestão só vale depois da revisão humana: confirmação ou correção, uma vez, registrada e auditada.</summary>
public sealed class ReviewTriageTests
{
    private readonly TriageApiFixture _api;
    private readonly Guid _clinic = Guid.NewGuid();

    public ReviewTriageTests(TriageApiFixture api)
    {
        _api = api;
        api.Reset();
    }

    private async Task<TriageResponse> PendingAsync(string complaint = "Dor de cabeça forte e piorando.")
    {
        var patient = await _api.ConsentingPatientAsync(_clinic);
        return await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, complaint);
    }

    [Fact]
    public async Task Confirm_KeepsTheSuggestion_RecordsTheReviewer_AndAudits()
    {
        // Arrange
        var pending = await PendingAsync();

        // Act
        var response = await _api.CreateClient(_clinic, Review).ReviewAsync(pending.Id, "confirm");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reviewed = (await response.Content.ReadFromJsonAsync<TriageResponse>(JsonSerializerOptions.Web, Ct))!;
        Assert.Equal(("confirmed", pending.SuggestedPriority, "usuario-de-teste"), (reviewed.Decision, reviewed.FinalPriority, reviewed.ReviewedBy));
        Assert.NotNull(reviewed.ReviewedAt);
        var audit = Assert.Single(await _api.OutboxAsync(_clinic), row => row.AuditAction == "triage.confirmed");
        Assert.Equal("usuario-de-teste", audit.Payload.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Correct_RecordsBothTheSuggestionAndTheCorrection()
    {
        // Arrange
        var pending = await PendingAsync();
        var other = pending.SuggestedPriority == "emergency" ? "urgent" : "emergency";

        // Act
        var response = await _api.CreateClient(_clinic, Review).ReviewAsync(pending.Id, "correct", other);

        // Assert
        var reviewed = (await response.Content.ReadFromJsonAsync<TriageResponse>(JsonSerializerOptions.Web, Ct))!;
        Assert.Equal(("corrected", pending.SuggestedPriority, other), (reviewed.Decision, reviewed.SuggestedPriority, reviewed.FinalPriority));
        Assert.Contains(await _api.OutboxAsync(_clinic), row => row.AuditAction == "triage.corrected");
    }

    [Fact]
    public async Task CorrectBelowAnAlarm_IsAllowed_TheProfessionalDecides()
    {
        // Arrange
        var pending = await PendingAsync("Dor no peito leve.");

        // Act
        var response = await _api.CreateClient(_clinic, Review).ReviewAsync(pending.Id, "correct", "less-urgent");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("approve", null, HttpStatusCode.BadRequest, "Triagem.DecisaoInvalida")]
    [InlineData(null, null, HttpStatusCode.BadRequest, "Triagem.DecisaoInvalida")]
    [InlineData("correct", null, HttpStatusCode.BadRequest, "Triagem.PrioridadeInvalida")]
    [InlineData("correct", "critical", HttpStatusCode.BadRequest, "Triagem.PrioridadeInvalida")]
    [InlineData("correct", "Urgent", HttpStatusCode.BadRequest, "Triagem.PrioridadeInvalida")]
    public async Task InvalidReview_IsRejected_WithoutChanges(string? decision, string? priority, HttpStatusCode status, string code)
    {
        // Arrange
        var pending = await PendingAsync();
        var client = _api.CreateClient(_clinic, Review);

        // Act
        var response = await client.ReviewAsync(pending.Id, decision, priority);

        // Assert
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await response.ReadProblemCodeAsync());
        Assert.Equal("pending", (await client.GetOkAsync(pending.Id)).Decision);
    }

    [Fact]
    public async Task CorrectToTheSamePriority_IsRejected()
    {
        // Arrange
        var pending = await PendingAsync();

        // Act
        var response = await _api.CreateClient(_clinic, Review).ReviewAsync(pending.Id, "correct", pending.SuggestedPriority);

        // Assert
        Assert.Equal("Triagem.CorrecaoIgualASugestao", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task ConfirmWithoutSuggestion_IsConflict_CorrectSetsIt()
    {
        // Arrange: modelo fora e nenhum alarme, então não há sugestão.
        _api.Model.Reply((_, _) => throw new HttpRequestException("fora"));
        var pending = await PendingAsync("Coceira no braço há uma semana.");
        var client = _api.CreateClient(_clinic, Review);

        // Act
        var confirm = await client.ReviewAsync(pending.Id, "confirm");
        var correct = await client.ReviewAsync(pending.Id, "correct", "non-urgent");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Equal("Triagem.SemSugestaoParaConfirmar", await confirm.ReadProblemCodeAsync());
        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
    }

    [Fact]
    public async Task SecondReview_IsConflict()
    {
        // Arrange
        var pending = await PendingAsync();
        var client = _api.CreateClient(_clinic, Review);
        await client.ReviewAsync(pending.Id, "confirm");

        // Act
        var second = await client.ReviewAsync(pending.Id, "correct", pending.SuggestedPriority == "urgent" ? "emergency" : "urgent");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("Triagem.JaRevisada", await second.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task ConcurrentReviews_OnlyOneWins()
    {
        // Arrange
        var pending = await PendingAsync();
        var client = _api.CreateClient(_clinic, Review);
        var other = pending.SuggestedPriority == "urgent" ? "emergency" : "urgent";

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            i % 2 == 0 ? client.ReviewAsync(pending.Id, "confirm") : client.ReviewAsync(pending.Id, "correct", other)));

        // Assert
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Single(await _api.OutboxAsync(_clinic), row => row.AuditAction is "triage.confirmed" or "triage.corrected");
    }

    [Fact]
    public async Task UnknownTriage_IsNotFound()
    {
        // Act
        var response = await _api.CreateClient(_clinic, Review).ReviewAsync(Guid.NewGuid(), "confirm");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
