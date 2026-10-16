using System.Net;
using System.Security.Cryptography;

using CareFlow.BuildingBlocks.AspNetCore.Security.ServiceTokens;
using CareFlow.Notifications.Api.Contacts;
using CareFlow.Notifications.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>
/// Cliente real do cofre (pipeline padrão do Microsoft.Extensions.Http.Resilience) contra um cofre simulado:
/// token de serviço por clínica, 404 como "sem contato", falhas como "tente depois" e nada de contato em log.
/// </summary>
public sealed class VaultContactDirectoryTests(NotificationsApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();
    private readonly Guid _patient = Guid.NewGuid();

    private static string ContactJson(string? phone, string? email) =>
        $$"""{"email":{{(email is null ? "null" : $"\"{email}\"")}},"phone":{{(phone is null ? "null" : $"\"{phone}\"")}}}""";

    [Fact]
    public async Task Found_ReturnsPhoneAndEmail_UsingAPerClinicServiceToken()
    {
        // Arrange
        var (phone, email) = (SyntheticPatients.Phone(), SyntheticPatients.Email());
        api.Vault.Respond(StubVault.Contact(ContactJson(phone, email)));

        // Act
        var lookup = await api.CreateVaultDirectory().FindAsync(_clinic, _patient, Ct);

        // Assert
        Assert.Equal(ContactLookupStatus.Found, lookup.Status);
        Assert.Equal((phone, email), (lookup.Contact!.Phone, lookup.Contact.Email));
        var (path, authorization) = Assert.Single(api.Vault.Requests);
        Assert.Equal($"/v1/patients/{_patient}/contact", path);
        Assert.StartsWith("Bearer ", authorization, StringComparison.Ordinal);

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(authorization!["Bearer ".Length..], new TokenValidationParameters
        {
            ValidIssuer = ServiceTokenOptions.DefaultIssuer,
            ValidAudience = VaultContactDirectory.Grant.Audience,
            IssuerSigningKey = new ECDsaSecurityKey(api.Vault.SigningKey),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });
        Assert.True(validation.IsValid, validation.Exception?.Message);
        Assert.Equal(
            ("careflow-notifications", _clinic.ToString(), "vault.contact.read"),
            ((string)validation.Claims["sub"], (string)validation.Claims["tenant_id"], (string)validation.Claims["scope"]));
        var token = (JsonWebToken)validation.SecurityToken;
        Assert.Equal(TimeSpan.FromMinutes(5), token.ValidTo - token.IssuedAt);
        Assert.DoesNotContain(api.Logs.Entries, entry => entry.OwnContent.Contains(phone, StringComparison.Ordinal) || entry.OwnContent.Contains(email, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PatientUnknownToTheVault_IsNotFound_WithoutRetrying()
    {
        // Arrange
        api.Vault.Respond(StubVault.Status(HttpStatusCode.NotFound));

        // Act
        var lookup = await api.CreateVaultDirectory().FindAsync(_clinic, _patient, Ct);

        // Assert
        Assert.Equal(ContactLookupStatus.NotFound, lookup.Status);
        Assert.Single(api.Vault.Requests);
    }

    [Fact]
    public async Task TransientErrors_AreRetried_ThenFound()
    {
        // Arrange
        api.Vault.Respond(StubVault.Status(HttpStatusCode.ServiceUnavailable), StubVault.Throw(), StubVault.Contact(ContactJson("5511912345678", null)));

        // Act
        var lookup = await api.CreateVaultDirectory().FindAsync(_clinic, _patient, Ct);

        // Assert
        Assert.Equal(("5511912345678", null), (lookup.Contact?.Phone, lookup.Contact?.Email));
        Assert.Equal(3, api.Vault.Requests.Count);
    }

    public static TheoryData<string> Failures => ["sempre-500", "sempre-fora", "recusado", "json-invalido"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task VaultFailures_AreUnavailable_NotMissingContact(string failure)
    {
        // Arrange
        Func<HttpRequestMessage, HttpResponseMessage>[] responses = failure switch
        {
            "sempre-500" => [StubVault.Status(HttpStatusCode.InternalServerError), StubVault.Status(HttpStatusCode.InternalServerError), StubVault.Status(HttpStatusCode.InternalServerError)],
            "sempre-fora" => [StubVault.Throw(), StubVault.Throw(), StubVault.Throw()],
            "recusado" => [StubVault.Status(HttpStatusCode.Forbidden)],
            _ => [StubVault.Contact("{nao-e-json")],
        };
        api.Vault.Respond(responses);

        // Act
        var lookup = await api.CreateVaultDirectory().FindAsync(_clinic, _patient, Ct);

        // Assert: 403 e JSON inválido não são transitórios, então não há retentativa.
        Assert.Equal(ContactLookupStatus.Unavailable, lookup.Status);
        Assert.Equal(responses.Length, api.Vault.Requests.Count);
    }

    [Fact]
    public async Task ServiceToken_IsCachedPerClinic()
    {
        // Arrange
        var directory = api.CreateVaultDirectory();
        var otherClinic = Guid.NewGuid();
        api.Vault.Respond(StubVault.Status(HttpStatusCode.NotFound), StubVault.Status(HttpStatusCode.NotFound), StubVault.Status(HttpStatusCode.NotFound));

        // Act
        await directory.FindAsync(_clinic, _patient, Ct);
        await directory.FindAsync(_clinic, Guid.NewGuid(), Ct);
        await directory.FindAsync(otherClinic, _patient, Ct);

        // Assert
        var tokens = api.Vault.Requests.Select(request => request.Authorization).ToArray();
        Assert.Equal(tokens[0], tokens[1]);
        Assert.NotEqual(tokens[0], tokens[2]);
    }

    [Fact]
    public void Issuer_RenewsTheTokenBeforeItExpires()
    {
        // Arrange
        var time = new ManualTime(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        using var issuer = new ServiceTokenIssuer(Options.Create(new ServiceTokenOptions { PrivateKey = api.Vault.PrivateKeyBase64 }), VaultContactDirectory.Grant, time);

        // Act
        var first = issuer.TokenFor(_clinic);
        time.Advance(TimeSpan.FromMinutes(3));
        var cached = issuer.TokenFor(_clinic);
        time.Advance(TimeSpan.FromMinutes(1));
        var renewed = issuer.TokenFor(_clinic);

        // Assert
        Assert.Equal(first, cached);
        Assert.NotEqual(first, renewed);
        Assert.Throws<ArgumentException>(() => issuer.TokenFor(Guid.Empty));
    }

    [Fact]
    public void Issuer_RefusesInvalidKeys()
    {
        // Arrange
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string[] invalid = ["nao-e-base64!", Convert.ToBase64String([1, 2, 3]), Convert.ToBase64String(p384.ExportPkcs8PrivateKey())];

        // Act / Assert
        Assert.All(invalid, key => Assert.Throws<InvalidOperationException>(
            () => new ServiceTokenIssuer(Options.Create(new ServiceTokenOptions { PrivateKey = key }), VaultContactDirectory.Grant, TimeProvider.System)));
    }

    [Fact]
    public void LocalIssuer_InProduction_IsRefused()
    {
        // Act
        var act = () => new ServiceCollection().AddContactDirectory(new FakeEnvironment(Environments.Production));

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CareFlow.Notifications.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
