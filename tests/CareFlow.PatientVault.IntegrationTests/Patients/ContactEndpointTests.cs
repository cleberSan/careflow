using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

using CareFlow.PatientVault.Api.Features.Patients;
using CareFlow.PatientVault.IntegrationTests.Infrastructure;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Patients;

/// <summary>
/// Leitura de contato para avisos: só telefone e e-mail, com token de serviço interno por clínica,
/// auditada a cada leitura e restrita a esse endpoint.
/// </summary>
public sealed class ContactEndpointTests(VaultApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    private async Task<(Guid PatientRef, RegisterPatientRequest Request)> RegisterAsync()
    {
        var request = NewPatient();
        var (patientRef, _) = await api.CreateClient(_clinic, Write).RegisterOkAsync(request);
        return (patientRef, request);
    }

    private Task<HttpResponseMessage> GetContactAsync(Guid patientRef, string token) =>
        api.CreateServiceClient(token).GetAsync($"/v1/patients/{patientRef}/contact", Ct);

    [Fact]
    public async Task ServiceToken_ReadsOnlyPhoneAndEmail_AndIsAudited()
    {
        // Arrange
        var (patientRef, request) = await RegisterAsync();

        // Act
        var response = await GetContactAsync(patientRef, api.InternalTokens.Issue(_clinic));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(request.FullName!, json, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Cpf!, json, StringComparison.Ordinal);
        var contact = await response.Content.ReadFromJsonAsync<PatientContactResponse>(Json, Ct);
        Assert.Equal((request.Email, request.Phone), (contact!.Email, contact.Phone));

        var audit = Assert.Single(await api.OutboxAsync(_clinic), row => row.AuditAction == "patient.contact.read");
        Assert.Equal("careflow-notifications", audit.Payload.GetProperty("actor").GetString());
        Assert.Equal(patientRef, audit.Payload.GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task ServiceTokenOfAnotherClinic_GetsNotFound()
    {
        // Arrange
        var (patientRef, _) = await RegisterAsync();

        // Act
        var response = await GetContactAsync(patientRef, api.InternalTokens.Issue(Guid.NewGuid()));

        // Assert: IDOR entre clínicas responde como paciente inexistente.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Paciente.NaoEncontrado", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task ServiceToken_CannotReadTheFullRecordOrWrite()
    {
        // Arrange: mesmo com os escopos de leitura e escrita, o token interno só vale para o contato.
        var (patientRef, _) = await RegisterAsync();
        var client = api.CreateServiceClient(api.InternalTokens.Issue(_clinic, scope: "vault.read vault.write vault.contact.read"));

        // Act
        var get = await client.GetAsync($"/v1/patients/{patientRef}", Ct);
        var lookup = await client.LookupAsync("52998224725");
        var register = await client.RegisterAsync(NewPatient());

        // Assert
        Assert.All([get, lookup, register], response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    [Fact]
    public async Task UserTokenWithReadScope_CannotUseTheContactEndpoint()
    {
        // Arrange
        var (patientRef, _) = await RegisterAsync();

        // Act
        var response = await api.CreateClient(_clinic, Read).GetAsync($"/v1/patients/{patientRef}/contact", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string> InvalidTokens => ["sem-escopo", "sem-clinica", "expirado", "outro-emissor", "outra-audiencia", "outra-chave"];

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task InvalidServiceToken_IsRejected(string variant)
    {
        // Arrange
        var (patientRef, _) = await RegisterAsync();
        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = variant switch
        {
            "sem-escopo" => api.InternalTokens.Issue(_clinic, scope: "vault.read"),
            "sem-clinica" => api.InternalTokens.Issue(tenantId: null),
            "expirado" => api.InternalTokens.Issue(_clinic, lifetime: TimeSpan.FromMinutes(-5)),
            "outro-emissor" => api.InternalTokens.Issue(_clinic, issuer: "emissor-desconhecido"),
            "outra-audiencia" => api.InternalTokens.Issue(_clinic, audience: "careflow-scheduling"),
            _ => api.InternalTokens.Issue(_clinic, signingKey: foreignKey),
        };

        // Act
        var response = await GetContactAsync(patientRef, token);

        // Assert
        var expected = variant is "sem-escopo" or "sem-clinica" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(await api.OutboxAsync(_clinic), row => row.AuditAction == "patient.contact.read");
    }
}
