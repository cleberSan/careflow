using System.Net;

using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.IntegrationTests.Infrastructure;

using static CareFlow.Triage.IntegrationTests.Infrastructure.TriageClient;

namespace CareFlow.Triage.IntegrationTests.Triages;

/// <summary>Fluxo principal: queixa, sugestão pendente de revisão, o que saiu para o modelo e a auditoria.</summary>
public sealed class SubmitTriageTests
{
    private readonly TriageApiFixture _api;
    private readonly Guid _clinic = Guid.NewGuid();

    public SubmitTriageTests(TriageApiFixture api)
    {
        _api = api;
        api.Reset();
    }

    [Fact]
    public async Task Submit_WithConsent_SuggestsFromTheModel_PendingReview()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic, age: 34);
        var client = _api.CreateClient(_clinic, Submit);

        // Act
        var triage = await client.SubmitOkAsync(patient, "Tosse seca e coriza há três dias, sem febre.");

        // Assert
        Assert.Equal(patient, triage.PatientRef);
        Assert.Equal("18-39", triage.AgeBand);
        Assert.Equal("model", triage.Source);
        Assert.Equal("accepted", triage.ModelStatus);
        Assert.Equal("modelo-de-teste", triage.ModelName);
        Assert.Equal(TriagePrompt.Version, triage.PromptVersion);
        Assert.NotNull(triage.SuggestedPriority);
        Assert.Equal(triage.ModelPriority, triage.SuggestedPriority);
        Assert.Equal("pending", triage.Decision);
        Assert.Null(triage.FinalPriority);
        Assert.Equal("Tosse seca e coriza há três dias, sem febre.", triage.SentToModel);

        var prompt = Assert.Single(_api.Model.Prompts);
        Assert.Equal(prompt.Digest, triage.PromptDigest);
        using var payload = System.Text.Json.JsonDocument.Parse(prompt.Payload);
        Assert.Equal("18-39", payload.RootElement.GetProperty("faixaEtaria").GetString());
        Assert.Equal("Tosse seca e coriza há três dias, sem febre.", payload.RootElement.GetProperty("queixa").GetString());
    }

    [Fact]
    public async Task Submit_AlarmSign_RaisesTheModelPriority_ByRule()
    {
        // Arrange: o modelo falso não conhece sinais de alarme e responde "não urgente".
        var patient = await _api.ConsentingPatientAsync(_clinic);

        // Act
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Aperto no peito leve desde ontem.");

        // Assert
        Assert.Equal("non-urgent", triage.ModelPriority);
        Assert.Equal("emergency", triage.SuggestedPriority);
        Assert.Equal("model-raised-by-rules", triage.Source);
        Assert.Contains("dor-no-peito", triage.AlarmSigns);
        Assert.Contains("alarm:prioridade-elevada-por-regra", triage.Guardrails);
    }

    [Fact]
    public async Task Submit_AuditsTheDigestOfWhatLeft_NeverTheText()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        const string complaint = "Dor de garganta e febre baixa há dois dias.";

        // Act
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, complaint);

        // Assert
        var outbox = await _api.OutboxAsync(_clinic);
        var disclosure = Assert.Single(outbox, row => row.Type == "audit.content-disclosed");
        Assert.Equal("triage.suggested.model", disclosure.AuditAction);
        Assert.Equal(triage.PromptDigest, disclosure.ContentDigest);
        Assert.Equal(patient.ToString(), disclosure.Payload.GetProperty("resourceId").GetString());
        Assert.DoesNotContain(outbox, row => row.Payload.GetRawText().Contains("garganta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Submit_WithoutModelCall_AuditsWithoutDigest()
    {
        // Arrange: sem consentimento, nada sai do serviço.
        var patient = _api.Profiles.Add(40, "Paciente Sem Consentimento");

        // Act
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Dor nas costas depois de carregar peso.");

        // Assert
        Assert.Null(triage.PromptDigest);
        var row = Assert.Single(await _api.OutboxAsync(_clinic));
        Assert.Equal(("audit.data-access", "triage.suggested.none"), (row.Type, row.AuditAction));
    }

    [Fact]
    public async Task Submit_CleansControlCharacters_BeforeEverything()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);

        // Act
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Tosse\u0000 há​ dois\tdias\r\n\r\n  e coriza.");

        // Assert
        Assert.Equal("Tosse há dois dias e coriza.", triage.Complaint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  a ")]
    [InlineData("\u0000\u0000\u0000\u0000")]
    public async Task Submit_InvalidComplaint_IsRejected(string? complaint)
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);

        // Act
        var response = await _api.CreateClient(_clinic, Submit).SubmitAsync(patient, complaint);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Triagem.QueixaInvalida", await response.ReadProblemCodeAsync());
        Assert.Empty(_api.Model.Prompts);
    }

    [Fact]
    public async Task Submit_ComplaintAtTheLimit_IsAccepted_AndOneOverIsRejected()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        var client = _api.CreateClient(_clinic, Submit);
        var atLimit = "tosse " + new string('a', TriageEngine.MaxComplaintLength - 6);

        // Act
        var accepted = await client.SubmitAsync(patient, atLimit);
        var rejected = await client.SubmitAsync(patient, atLimit + "a");

        // Assert
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Submit_UnknownPatient_IsNotFound()
    {
        // Act
        var response = await _api.CreateClient(_clinic, Submit).SubmitAsync(Guid.NewGuid(), "Tosse há dois dias.");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Triagem.PacienteNaoEncontrado", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Submit_EmptyPatient_IsRejected()
    {
        // Act
        var response = await _api.CreateClient(_clinic, Submit).SubmitAsync(Guid.Empty, "Tosse há dois dias.");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Triagem.IdentificadorAusente", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Submit_VaultUnavailable_Is503_WithRetryAfter_AndNothingIsSaved()
    {
        // Arrange
        var patient = _api.Profiles.AddUnavailable();

        // Act
        var response = await _api.CreateClient(_clinic, Submit).SubmitAsync(patient, "Tosse há dois dias.");

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Triagem.CofreIndisponivel", await response.ReadProblemCodeAsync());
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Empty(await _api.OutboxAsync(_clinic));
    }

    [Fact]
    public async Task Get_ReturnsTheFullCase_AndAuditsTheRead()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        var created = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Tontura ao levantar desde hoje cedo.");

        // Act
        var read = await _api.CreateClient(_clinic, Read).GetOkAsync(created.Id);

        // Assert
        Assert.Equal(created with { Masks = read.Masks, Guardrails = read.Guardrails, AlarmSigns = read.AlarmSigns }, read);
        Assert.Equal(created.Masks, read.Masks);
        Assert.Contains(await _api.OutboxAsync(_clinic), row => row.AuditAction == "triage.read");
    }

    [Fact]
    public async Task SubmissionsPerClinic_AreRateLimited_WithoutAffectingOtherClinics()
    {
        // Arrange
        var busy = Guid.NewGuid();
        var patient = await _api.ConsentingPatientAsync(busy);
        var client = _api.CreateClient(busy, Submit);
        for (var i = 0; i < TriageApiFixture.SubmissionsPerMinute; i++)
        {
            await client.SubmitAsync(patient, "Tosse há dois dias.");
        }

        // Act
        var limited = await client.SubmitAsync(patient, "Tosse há dois dias.");
        var other = await _api.CreateClient(_clinic, Submit).SubmitAsync(await _api.ConsentingPatientAsync(_clinic), "Tosse há dois dias.");

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }
}
