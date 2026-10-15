using System.Net.Http.Json;

using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts.Consents;
using CareFlow.Triage.Api.Features.Consents;
using CareFlow.Triage.IntegrationTests.Infrastructure;

using static CareFlow.Triage.IntegrationTests.Infrastructure.TriageClient;

namespace CareFlow.Triage.IntegrationTests.Triages;

/// <summary>Sem consentimento ai-triage ativo, a queixa não sai do serviço; a revogação vale para as próximas triagens.</summary>
public sealed class ConsentTests
{
    private readonly TriageApiFixture _api;
    private readonly Guid _clinic = Guid.NewGuid();

    public ConsentTests(TriageApiFixture api)
    {
        _api = api;
        api.Reset();
    }

    [Fact]
    public async Task NoConsentEvent_NoModelCall_AlarmRulesStillSuggest()
    {
        // Arrange
        var patient = _api.Profiles.Add(52, "Paciente Sintetico Sem Evento");

        // Act
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Falta de ar desde a madrugada.");

        // Assert
        Assert.Empty(_api.Model.Prompts);
        Assert.Equal("skipped-no-consent", triage.ModelStatus);
        Assert.Contains("consent:sem-consentimento-ai-triage", triage.Guardrails);
        Assert.Equal(("emergency", "rules"), (triage.SuggestedPriority, triage.Source));
        Assert.Null(triage.ModelName);
    }

    [Fact]
    public async Task Revocation_StopsTheNextCalls_AndAnOldGrantDoesNotUndoIt()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        var client = _api.CreateClient(_clinic, Submit);
        await client.SubmitOkAsync(patient, "Tosse há dois dias.");

        // Act
        await _api.RevokeAiTriageAsync(_clinic, patient, version: 2);
        var late = await _api.GrantAiTriageAsync(_clinic, patient, version: 1);
        var afterRevocation = await client.SubmitOkAsync(patient, "Tosse há três dias.");

        // Assert
        Assert.Equal(ProcessOutcome.Processed, late);
        Assert.Single(_api.Model.Prompts);
        Assert.Equal("skipped-no-consent", afterRevocation.ModelStatus);
    }

    [Fact]
    public async Task RegrantAfterRevocation_AllowsTheModelAgain()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        await _api.RevokeAiTriageAsync(_clinic, patient, version: 2);

        // Act
        await _api.GrantAiTriageAsync(_clinic, patient, version: 3);
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Tosse há dois dias.");

        // Assert
        Assert.Equal("accepted", triage.ModelStatus);
    }

    [Fact]
    public async Task OtherPurposes_AndOtherClinics_DoNotCount()
    {
        // Arrange
        var patient = _api.Profiles.Add(30, "Paciente Sintetico Outra Finalidade");
        await _api.DeliverAsync(_clinic, new ConsentGranted(patient, ConsentPurposes.Notifications, "consent", "v1", 1, DateTimeOffset.UtcNow));
        await _api.DeliverAsync(_clinic, new ConsentRevoked(patient, ConsentPurposes.Scheduling, 2, DateTimeOffset.UtcNow));
        await _api.GrantAiTriageAsync(Guid.NewGuid(), patient);

        // Act
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Tosse há dois dias.");

        // Assert
        Assert.Equal("skipped-no-consent", triage.ModelStatus);
        Assert.Empty(_api.Model.Prompts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidConsentVersion_IsPoison(long version)
    {
        // Act
        var outcome = await _api.GrantAiTriageAsync(_clinic, Guid.NewGuid(), version);

        // Assert
        Assert.Equal(ProcessOutcome.Invalid, outcome);
    }

    [Fact]
    public async Task EmptyPatient_IsPoison()
    {
        // Act
        var outcome = await _api.RevokeAiTriageAsync(_clinic, Guid.Empty);

        // Assert
        Assert.Equal(ProcessOutcome.Invalid, outcome);
    }

    [Fact]
    public async Task ConcurrentGrantAndRevoke_TheHighestVersionWins()
    {
        // Arrange
        var patient = _api.Profiles.Add(30, "Paciente Sintetico Concorrente");

        // Act
        await Task.WhenAll(
            _api.GrantAiTriageAsync(_clinic, patient, version: 1),
            _api.RevokeAiTriageAsync(_clinic, patient, version: 2),
            _api.GrantAiTriageAsync(_clinic, patient, version: 3));

        // Assert
        var state = await _api.ScalarAsAdminAsync("SELECT state || ':' || version FROM triage_consents WHERE patient_ref = $1", patient);
        Assert.Equal("Granted:3", state);
    }

    [Fact]
    public async Task AiConsent_ShowsTheProjection_AndHidesOtherClinics()
    {
        // Arrange
        var patient = Guid.NewGuid();
        var reader = _api.CreateClient(_clinic, Read);

        // Act
        var before = await reader.GetFromJsonAsync<AiConsentResponse>($"/v1/patients/{patient}/ai-consent", Ct);
        await _api.GrantAiTriageAsync(_clinic, patient, version: 1);
        var granted = await reader.GetFromJsonAsync<AiConsentResponse>($"/v1/patients/{patient}/ai-consent", Ct);
        await _api.RevokeAiTriageAsync(_clinic, patient, version: 2);
        var revoked = await reader.GetFromJsonAsync<AiConsentResponse>($"/v1/patients/{patient}/ai-consent", Ct);
        var otherClinic = await _api.CreateClient(Guid.NewGuid(), Read).GetFromJsonAsync<AiConsentResponse>($"/v1/patients/{patient}/ai-consent", Ct);
        var submitterOnly = await _api.CreateClient(_clinic, Submit).GetAsync($"/v1/patients/{patient}/ai-consent", Ct);

        // Assert
        Assert.Equal(("unknown", 0L), (before!.State, before.Version));
        Assert.Equal(("granted", 1L), (granted!.State, granted.Version));
        Assert.Equal(("revoked", 2L), (revoked!.State, revoked.Version));
        Assert.Equal(("unknown", 0L, (DateTimeOffset?)null), (otherClinic!.State, otherClinic.Version, otherClinic.UpdatedAt));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, submitterOnly.StatusCode);
    }
}
