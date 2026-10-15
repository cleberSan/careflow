using System.Diagnostics;
using System.Security.Cryptography;

using CareFlow.Demo.Api.Common;
using CareFlow.Demo.Api.Services;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CareFlow.Demo.UnitTests.Services;

public sealed class DemoTokenIssuerTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static (DemoTokenIssuer Issuer, ManualTime Time) Create()
    {
        var time = new ManualTime(Start);
        var options = new DemoTokenOptions { SigningKey = Convert.ToBase64String(Key), Lifetime = TimeSpan.FromMinutes(2) };
        return (new DemoTokenIssuer(Options.Create(options), time), time);
    }

    [Fact]
    public async Task TokenFor_IsSignedForTheAudience_WithTenantAndSortedScopes()
    {
        // Arrange
        var (issuer, _) = Create();
        var clinic = Guid.NewGuid();

        // Act
        var token = issuer.TokenFor(new ServiceCall(clinic, "careflow-scheduling", ["scheduling.write", "scheduling.read"]));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = DemoTokenOptions.DefaultIssuer,
            ValidAudience = "careflow-scheduling",
            IssuerSigningKey = new SymmetricSecurityKey(Key),
            ValidateLifetime = false,
        });

        // Assert
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(clinic.ToString(), result.Claims["tenant_id"]);
        Assert.Equal("scheduling.read scheduling.write", result.Claims["scope"]);
        Assert.Equal(DemoTokenIssuer.Subject, result.Claims["sub"]);
        var jwt = new JsonWebToken(token);
        Assert.Equal(TimeSpan.FromMinutes(2), jwt.ValidTo - jwt.ValidFrom);
    }

    [Fact]
    public void TokenFor_IsCachedUntilTheRefreshMargin_ThenRenewed()
    {
        // Arrange
        var (issuer, time) = Create();
        var call = new ServiceCall(Guid.NewGuid(), "careflow-vault", ["vault.read"]);

        // Act
        var first = issuer.TokenFor(call);
        time.Advance(TimeSpan.FromSeconds(89));
        var cached = issuer.TokenFor(call);
        time.Advance(TimeSpan.FromSeconds(1));
        var renewed = issuer.TokenFor(call);

        // Assert
        Assert.Same(first, cached);
        Assert.NotEqual(first, renewed);
    }

    [Fact]
    public void Forget_DropsOnlyThatClinicsTokens()
    {
        // Arrange
        var (issuer, _) = Create();
        var gone = new ServiceCall(Guid.NewGuid(), "careflow-vault", ["vault.read"]);
        var kept = new ServiceCall(Guid.NewGuid(), "careflow-vault", ["vault.read"]);
        var goneToken = issuer.TokenFor(gone);
        var keptToken = issuer.TokenFor(kept);

        // Act
        issuer.Forget(gone.Clinic);

        // Assert
        Assert.NotEqual(goneToken, issuer.TokenFor(gone));
        Assert.Same(keptToken, issuer.TokenFor(kept));
    }

    [Fact]
    public void TokenFor_WithoutClinic_Throws()
    {
        var (issuer, _) = Create();

        Assert.Throws<ArgumentException>(() => issuer.TokenFor(new ServiceCall(Guid.Empty, "careflow-vault", [])));
    }

    [Fact]
    public void Authenticate_SetsTheBearerHeader()
    {
        var (issuer, _) = Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://vault/");

        issuer.Authenticate(request, new ServiceCall(Guid.NewGuid(), "careflow-vault", ["vault.read"]));

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
    }

    [Theory]
    [InlineData("não é base64")]
    [InlineData("c2hvcnQ=")]
    public void DecodeKey_RejectsInvalidOrShortKeys(string key) =>
        Assert.Throws<InvalidOperationException>(() => DemoTokenIssuer.DecodeKey(key));

    [Fact]
    public void InboundIgnoringPropagator_ExtractsNothing_ButStillInjects()
    {
        // Arrange
        var propagator = new InboundIgnoringPropagator(DistributedContextPropagator.CreateW3CPropagator());
        var incoming = new Dictionary<string, string> { ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", ["baggage"] = "cpf=1" };
        using var activity = new Activity("saida").SetIdFormat(ActivityIdFormat.W3C).Start();
        var outgoing = new Dictionary<string, string>();

        // Act
        propagator.ExtractTraceIdAndState(incoming, Get, out var traceId, out var traceState);
        var baggage = propagator.ExtractBaggage(incoming, Get);
        propagator.Inject(activity, outgoing, (carrier, name, value) => ((Dictionary<string, string>)carrier!)[name] = value);

        // Assert
        Assert.Null(traceId);
        Assert.Null(traceState);
        Assert.Null(baggage);
        Assert.Contains("traceparent", propagator.Fields);
        Assert.Contains(activity.TraceId.ToHexString(), outgoing["traceparent"], StringComparison.Ordinal);

        static void Get(object? carrier, string name, out string? value, out IEnumerable<string>? values)
        {
            values = null;
            value = ((Dictionary<string, string>)carrier!).GetValueOrDefault(name);
        }
    }
}
