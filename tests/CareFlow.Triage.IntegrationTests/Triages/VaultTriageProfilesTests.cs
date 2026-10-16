using System.Net;

using CareFlow.BuildingBlocks.AspNetCore.Security.ServiceTokens;
using CareFlow.Triage.Api.Profiles;
using CareFlow.Triage.IntegrationTests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using static CareFlow.Triage.IntegrationTests.Infrastructure.TriageClient;

namespace CareFlow.Triage.IntegrationTests.Triages;

/// <summary>
/// Cliente real do cofre contra um cofre simulado: token de serviço só com o escopo do perfil de triagem, idade
/// virando faixa, 404 como "paciente desconhecido" e falhas como "tente depois", sem nome em log.
/// </summary>
public sealed class VaultTriageProfilesTests(TriageApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();
    private readonly Guid _patient = Guid.NewGuid();

    [Fact]
    public async Task Found_MapsAgeToBand_KeepsTheName_UsingAScopedServiceToken()
    {
        // Arrange
        api.Vault.Respond(StubVault.Json("""{"age":67,"fullName":"Helena Sintetica Prado"}"""));

        // Act
        var lookup = await api.CreateVaultProfiles().FindAsync(_clinic, _patient, Ct);

        // Assert
        Assert.Equal(TriageProfileStatus.Found, lookup.Status);
        Assert.Equal("60-79", lookup.Profile!.AgeBand.Name);
        Assert.DoesNotContain("Helena", lookup.Profile.ToString(), StringComparison.Ordinal);
        var (path, authorization) = Assert.Single(api.Vault.Requests);
        Assert.Equal($"/v1/patients/{_patient}/triage-profile", path);

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(authorization!["Bearer ".Length..], new TokenValidationParameters
        {
            ValidIssuer = ServiceTokenOptions.DefaultIssuer,
            ValidAudience = "careflow-patient-vault",
            IssuerSigningKey = new ECDsaSecurityKey(api.Vault.SigningKey),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });
        Assert.True(validation.IsValid, validation.Exception?.Message);
        Assert.Equal(
            ("careflow-triage", _clinic.ToString(), "vault.triage-profile.read"),
            ((string)validation.Claims["sub"], (string)validation.Claims["tenant_id"], (string)validation.Claims["scope"]));
        Assert.DoesNotContain(api.Logs.Entries, entry => entry.OwnContent.Contains("Helena", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownPatient_IsNotFound()
    {
        // Arrange
        api.Vault.Respond(StubVault.Status(HttpStatusCode.NotFound));

        // Act
        var lookup = await api.CreateVaultProfiles().FindAsync(_clinic, _patient, Ct);

        // Assert
        Assert.Equal(TriageProfileStatus.NotFound, lookup.Status);
    }

    public static TheoryData<string> Failures => ["sempre-500", "sempre-fora", "recusado", "json-invalido", "idade-invalida", "corpo-nulo"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task VaultFailures_AreUnavailable(string failure)
    {
        // Arrange
        Func<HttpRequestMessage, HttpResponseMessage>[] responses = failure switch
        {
            "sempre-500" => [StubVault.Status(HttpStatusCode.InternalServerError), StubVault.Status(HttpStatusCode.InternalServerError), StubVault.Status(HttpStatusCode.InternalServerError)],
            "sempre-fora" => [StubVault.Throw(), StubVault.Throw(), StubVault.Throw()],
            "recusado" => [StubVault.Status(HttpStatusCode.Forbidden)],
            "idade-invalida" => [StubVault.Json("""{"age":-3,"fullName":null}""")],
            "corpo-nulo" => [StubVault.Json("null")],
            _ => [StubVault.Json("{nao-e-json")],
        };
        api.Vault.Respond(responses);

        // Act
        var lookup = await api.CreateVaultProfiles().FindAsync(_clinic, _patient, Ct);

        // Assert
        Assert.Equal(TriageProfileStatus.Unavailable, lookup.Status);
        Assert.Equal(responses.Length, api.Vault.Requests.Count);
    }

    [Fact]
    public void LocalIssuer_InProduction_IsRefused()
    {
        // Act
        var act = () => new ServiceCollection().AddTriageProfiles(new FakeEnvironment(Environments.Production));

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CareFlow.Triage.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
