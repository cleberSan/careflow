using System.Net;
using System.Net.Http.Json;

using CareFlow.PatientVault.Api.Features.Patients;
using CareFlow.PatientVault.IntegrationTests.Infrastructure;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Patients;

/// <summary>
/// Perfil de triagem (fase 7): idade em anos e nome, nunca data de nascimento, CPF ou contato; escopo próprio,
/// token de serviço interno por clínica e auditoria a cada leitura.
/// </summary>
public sealed class TriageProfileEndpointTests(VaultApiFixture api)
{
    private const string Scope = "vault.triage-profile.read";

    private readonly Guid _clinic = Guid.NewGuid();

    private async Task<(Guid PatientRef, RegisterPatientRequest Request)> RegisterAsync()
    {
        var request = NewPatient();
        var (patientRef, _) = await api.CreateClient(_clinic, Write).RegisterOkAsync(request);
        return (patientRef, request);
    }

    private Task<HttpResponseMessage> GetProfileAsync(Guid patientRef, string token) =>
        api.CreateServiceClient(token).GetAsync($"/v1/patients/{patientRef}/triage-profile", Ct);

    [Fact]
    public async Task ServiceToken_ReadsOnlyAgeAndName_AndIsAudited()
    {
        // Arrange
        var (patientRef, request) = await RegisterAsync();

        // Act
        var response = await GetProfileAsync(patientRef, api.InternalTokens.Issue(_clinic, scope: Scope, subject: "careflow-triage"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(request.Cpf!, json, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Email!, json, StringComparison.Ordinal);
        Assert.DoesNotContain(request.BirthDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), json, StringComparison.Ordinal);
        var profile = await response.Content.ReadFromJsonAsync<TriageProfileResponse>(Json, Ct);
        Assert.Equal(request.FullName, profile!.FullName);
        Assert.Equal(GetTriageProfileEndpoint.AgeOn(request.BirthDate, DateOnly.FromDateTime(DateTime.UtcNow)), profile.Age);
        Assert.Equal("TriageProfileResponse { *** }", profile.ToString());

        var audit = Assert.Single(await api.OutboxAsync(_clinic), row => row.AuditAction == "patient.triage-profile.read");
        Assert.Equal("careflow-triage", audit.Payload.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task ContactScope_CannotReadTheTriageProfile_AndViceVersa()
    {
        // Arrange
        var (patientRef, _) = await RegisterAsync();

        // Act
        var profileWithContactScope = await GetProfileAsync(patientRef, api.InternalTokens.Issue(_clinic));
        var contactWithProfileScope = await api.CreateServiceClient(api.InternalTokens.Issue(_clinic, scope: Scope))
            .GetAsync($"/v1/patients/{patientRef}/contact", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, profileWithContactScope.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, contactWithProfileScope.StatusCode);
    }

    [Fact]
    public async Task OtherClinic_GetsNotFound()
    {
        var (patientRef, _) = await RegisterAsync();

        var response = await GetProfileAsync(patientRef, api.InternalTokens.Issue(Guid.NewGuid(), scope: Scope));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("2000-10-15", "2026-10-15", 26)]
    [InlineData("2000-10-16", "2026-10-15", 25)]
    [InlineData("2026-10-16", "2026-10-15", 0)]
    public void AgeOn_CountsCompletedYears(string birth, string today, int age)
    {
        Assert.Equal(age, GetTriageProfileEndpoint.AgeOn(DateOnly.Parse(birth, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
