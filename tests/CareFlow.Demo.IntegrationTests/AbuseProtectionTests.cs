using System.Net;
using System.Text;

using CareFlow.Demo.IntegrationTests.Infrastructure;

namespace CareFlow.Demo.IntegrationTests;

/// <summary>
/// Proteção contra abuso do Demo publicado (ADR-0013): teto por endereço de origem em todas as rotas, teto por
/// sessão (por clínica), cabeçalho encaminhado aceito só do proxy, corpo limitado e CORS só para o site.
/// </summary>
public sealed class AbuseProtectionTests
{
    private const string ProxyNetwork = "127.0.0.0/8";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PerAddressLimit_CountsAllSessionsOfTheSameVisitor()
    {
        // Arrange: um visitante atrás do proxy com duas sessões; o teto do endereço vale para as duas juntas.
        await using var demo = DemoHost.Start(
            ("Demo:Proxy:KnownNetworks:0", ProxyNetwork),
            ("Demo:RateLimits:RequestsPerMinutePerAddress", "6"),
            ("Demo:RateLimits:RequestsPerMinute", "100"));
        using var first = demo.Visitor(forwardedFor: "203.0.113.10");
        using var second = demo.Visitor(forwardedFor: "203.0.113.10");
        await DemoHost.OpenAsync(first);
        await DemoHost.OpenAsync(second);

        // Act: 2 aberturas + 4 leituras esgotam as 6 fichas; a próxima, de qualquer sessão, passa do teto.
        var statuses = new List<HttpStatusCode>();
        foreach (var client in new[] { first, second, first, second, first })
        {
            using var response = await client.GetAsync("demo/v1/session", Ct);
            statuses.Add(response.StatusCode);
        }

        // Assert
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task PerAddressLimit_IsSeparatePerVisitor()
    {
        // Arrange
        await using var demo = DemoHost.Start(
            ("Demo:Proxy:KnownNetworks:0", ProxyNetwork),
            ("Demo:RateLimits:RequestsPerMinutePerAddress", "2"));
        using var alice = demo.Visitor(forwardedFor: "203.0.113.20");
        using var bruno = demo.Visitor(forwardedFor: "203.0.113.21");

        // Act
        var aliceStatuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await alice.GetAsync("demo/v1/status", Ct);
            aliceStatuses.Add(response.StatusCode);
        }

        using var brunoResponse = await bruno.GetAsync("demo/v1/status", Ct);

        // Assert
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], aliceStatuses);
        Assert.Equal(HttpStatusCode.OK, brunoResponse.StatusCode);
    }

    [Fact]
    public async Task ForgedForwardedFor_FromOutsideTheProxyNetwork_DoesNotEscapeTheLimit()
    {
        // Arrange: o proxy configurado é outra rede; quem chama direto não escolhe o próprio endereço.
        await using var demo = DemoHost.Start(
            ("Demo:Proxy:KnownNetworks:0", "10.99.0.0/16"),
            ("Demo:RateLimits:RequestsPerMinutePerAddress", "2"));

        // Act
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var visitor = demo.Visitor(forwardedFor: $"198.51.100.{i}");
            using var response = await visitor.GetAsync("demo/v1/status", Ct);
            statuses.Add(response.StatusCode);
        }

        // Assert
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task ForwardedChain_OnlyTheHopAddedByTheProxyCounts()
    {
        // Arrange: o navegador inventa um endereço à esquerda; o proxy acrescenta o real à direita.
        await using var demo = DemoHost.Start(
            ("Demo:Proxy:KnownNetworks:0", ProxyNetwork),
            ("Demo:RateLimits:RequestsPerMinutePerAddress", "2"));

        // Act
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var visitor = demo.Visitor(forwardedFor: $"192.0.2.{i}, 203.0.113.30");
            using var response = await visitor.GetAsync("demo/v1/status", Ct);
            statuses.Add(response.StatusCode);
        }

        // Assert
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task PerSessionLimit_CapsOneClinic_WithoutTouchingAnother()
    {
        // Arrange
        await using var demo = DemoHost.Start(
            ("Demo:RateLimits:RequestsPerMinute", "3"),
            ("Demo:RateLimits:RequestsPerMinutePerAddress", "1000"));
        using var first = demo.Visitor();
        using var second = demo.Visitor();
        await DemoHost.OpenAsync(first);
        await DemoHost.OpenAsync(second);

        // Act
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await first.GetAsync("demo/v1/session", Ct);
            statuses.Add(response.StatusCode);
        }

        using var other = await second.GetAsync("demo/v1/session", Ct);

        // Assert
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task SessionCreation_IsCappedPerAddress()
    {
        // Arrange
        await using var demo = DemoHost.Start(("Demo:RateLimits:SessionsPerWindow", "2"));
        using var visitor = demo.Visitor();

        // Act
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await visitor.PostAsync("demo/v1/sessions", null, Ct);
            statuses.Add(response.StatusCode);
        }

        // Assert
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task OversizedBody_IsRefusedBeforeTheHandler()
    {
        // Arrange
        await using var demo = DemoHost.Start();
        using var visitor = demo.Visitor();
        await DemoHost.OpenAsync(visitor);
        using var body = new StringContent($$"""{ "complaint": "{{new string('a', 8 * 1024)}}" }""", Encoding.UTF8, "application/json");

        // Act
        using var response = await visitor.PostAsync("demo/v1/triages", body, Ct);

        // Assert
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("Bearer curto")]
    [InlineData("Basic dXNlcjpzZW5oYQ==")]
    [InlineData("Bearer AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task UnknownOrMalformedToken_IsUnauthorized(string authorization)
    {
        // Arrange
        await using var demo = DemoHost.Start();
        using var visitor = demo.Visitor();
        visitor.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);

        // Act
        using var response = await visitor.GetAsync("demo/v1/session", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Preflight_FromTheSite_IsAllowed_WithoutCredentials()
    {
        // Arrange
        await using var demo = DemoHost.Start();
        using var client = new HttpClient { BaseAddress = demo.BaseAddress };
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "demo/v1/sessions");
        preflight.Headers.Add("Origin", DemoHost.SiteOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,x-careflow-demo");

        // Act
        using var response = await client.SendAsync(preflight, Ct);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(DemoHost.SiteOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Preflight_FromAnotherSite_GetsNoPermission()
    {
        // Arrange
        await using var demo = DemoHost.Start();
        using var client = new HttpClient { BaseAddress = demo.BaseAddress };
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "demo/v1/sessions");
        preflight.Headers.Add("Origin", "https://outro.exemplo");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        // Act
        using var response = await client.SendAsync(preflight, Ct);

        // Assert
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task RefusedRequests_StillCarryCors_SoTheSiteCanReadThe429()
    {
        // Arrange
        await using var demo = DemoHost.Start(("Demo:RateLimits:RequestsPerMinutePerAddress", "1"));
        using var visitor = demo.Visitor();
        visitor.DefaultRequestHeaders.Add("Origin", DemoHost.SiteOrigin);
        using var allowed = await visitor.GetAsync("demo/v1/status", Ct);

        // Act
        using var refused = await visitor.GetAsync("demo/v1/status", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(DemoHost.SiteOrigin, Assert.Single(refused.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.NotNull(refused.Headers.RetryAfter);
    }
}
