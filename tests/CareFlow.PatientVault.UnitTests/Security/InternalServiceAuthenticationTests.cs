using System.Security.Cryptography;

using CareFlow.PatientVault.Api.Common.Security;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CareFlow.PatientVault.UnitTests.Security;

public sealed class InternalServiceAuthenticationTests
{
    private static IConfiguration Config(string? publicKey, string? issuer = null, string? additional = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalAuth:PublicKey"] = publicKey,
                ["InternalAuth:Issuer"] = issuer,
                ["InternalAuth:AdditionalPublicKeys:0"] = additional,
            })
            .Build();

    private static string NewPublicKey(ECCurve curve)
    {
        using var key = ECDsa.Create(curve);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private static JwtBearerOptions Register(IConfiguration configuration, string environment)
    {
        var services = new ServiceCollection();
        services.AddAuthentication().AddInternalServiceTokens(configuration, new FakeEnvironment(environment));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(InternalServiceAuthentication.SchemeName);
    }

    [Fact]
    public void ValidKey_ConfiguresStrictEs256Validation()
    {
        // Act
        var options = Register(Config(NewPublicKey(ECCurve.NamedCurves.nistP256), issuer: "emissor-dev"), Environments.Development);

        // Assert
        var parameters = options.TokenValidationParameters;
        Assert.IsType<ECDsaSecurityKey>(Assert.Single(parameters.IssuerSigningKeys));
        Assert.Equal(("emissor-dev", "careflow-patient-vault"), (parameters.ValidIssuer, parameters.ValidAudience));
        Assert.Equal([SecurityAlgorithms.EcdsaSha256], parameters.ValidAlgorithms);
        Assert.True(parameters.RequireExpirationTime && parameters.ValidateLifetime && parameters.RequireSignedTokens);
        Assert.False(options.MapInboundClaims);
    }

    [Fact]
    public void WithoutKey_SchemeExistsButTrustsNoSigner()
    {
        // Act
        var options = Register(Config(publicKey: null), Environments.Development);

        // Assert
        Assert.Null(options.TokenValidationParameters.IssuerSigningKey);
        Assert.Empty(options.TokenValidationParameters.IssuerSigningKeys);
        Assert.Equal(InternalServiceAuthentication.DefaultIssuer, options.TokenValidationParameters.ValidIssuer);
    }

    [Fact]
    public void OneKeyPerCallingService_AreAllTrusted()
    {
        // Act: Notifications e Triage, cada um com o próprio par (ADR-0013).
        var options = Register(
            Config(NewPublicKey(ECCurve.NamedCurves.nistP256), additional: NewPublicKey(ECCurve.NamedCurves.nistP256)),
            "Demo");

        // Assert
        Assert.Equal(2, options.TokenValidationParameters.IssuerSigningKeys.Count());
    }

    [Fact]
    public void AdditionalKeyAlone_InProduction_IsRefused()
    {
        // Act
        var act = () => Register(Config(publicKey: null, additional: NewPublicKey(ECCurve.NamedCurves.nistP256)), Environments.Production);

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void LocalIssuerKey_InProduction_IsRefused()
    {
        // Act
        var act = () => new ServiceCollection().AddAuthentication()
            .AddInternalServiceTokens(Config(NewPublicKey(ECCurve.NamedCurves.nistP256)), new FakeEnvironment(Environments.Production));

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    [Theory]
    [InlineData("nao-e-base64!")]
    [InlineData("AAAA")]
    public void MalformedKey_IsRefusedAtRegistration(string publicKey)
    {
        // Act
        var act = () => new ServiceCollection().AddAuthentication()
            .AddInternalServiceTokens(Config(publicKey), new FakeEnvironment(Environments.Development));

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void KeyOnAnotherCurve_IsRefused()
    {
        // Act
        var act = () => new ServiceCollection().AddAuthentication()
            .AddInternalServiceTokens(Config(NewPublicKey(ECCurve.NamedCurves.nistP384)), new FakeEnvironment(Environments.Development));

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CareFlow.PatientVault.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
