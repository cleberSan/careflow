using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CareFlow.SmokeTests;

/// <summary>
/// A leitura da configuração roda sempre, mesmo sem deploy: sem ela o projeto não executaria nenhum teste no CI
/// e um erro de configuração só apareceria depois de um deploy.
/// </summary>
public sealed class SmokeTargetTests
{
    private const string Url = "https://demo.careflow.test";
    private const string Origin = "https://site.careflow.test";

    [Fact]
    public void WithoutUrl_ThereIsNoTarget()
    {
        Assert.Null(SmokeTarget.FromSettings(Settings()));
        Assert.Null(SmokeTarget.FromSettings(Settings(("CAREFLOW_SMOKE_URL", "  "))));
    }

    [Fact]
    public void PlainHttp_IsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SmokeTarget.FromSettings(
            Settings(("CAREFLOW_SMOKE_URL", "http://demo.careflow.test"), ("CAREFLOW_SMOKE_ORIGIN", Origin))));

        Assert.Contains("https", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrigin_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => SmokeTarget.FromSettings(Settings(("CAREFLOW_SMOKE_URL", Url))));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:porta")]
    [InlineData(":443")]
    public void ConnectWithoutPort_IsRefused(string connect)
    {
        Assert.Throws<InvalidOperationException>(() => SmokeTarget.FromSettings(Settings(
            ("CAREFLOW_SMOKE_URL", Url), ("CAREFLOW_SMOKE_ORIGIN", Origin), ("CAREFLOW_SMOKE_CONNECT", connect))));
    }

    [Fact]
    public void ValidSettings_NormalizeTheAddressAndTheOrigin()
    {
        using var target = SmokeTarget.FromSettings(Settings(
            ("CAREFLOW_SMOKE_URL", Url + "/"), ("CAREFLOW_SMOKE_ORIGIN", Origin + "/"), ("CAREFLOW_SMOKE_CONNECT", "127.0.0.1:443")));

        Assert.NotNull(target);
        Assert.Equal(new Uri(Url + "/"), target.BaseAddress);
        Assert.Equal(Origin, target.Origin);
        Assert.Equal(target.BaseAddress, target.Client.BaseAddress);
    }

    [Fact]
    public void SiteRequest_CarriesTheDemoHeaderTheOriginAndTheToken()
    {
        using var target = SmokeTarget.FromSettings(Settings(("CAREFLOW_SMOKE_URL", Url), ("CAREFLOW_SMOKE_ORIGIN", Origin)))!;

        using var anonymous = target.FromSite(HttpMethod.Get, "demo/v1/status");
        using var authenticated = target.FromSite(HttpMethod.Post, "demo/v1/sessions/end", "token");

        Assert.Equal("1", Assert.Single(anonymous.Headers.GetValues(SmokeTarget.DemoHeader)));
        Assert.Equal(Origin, Assert.Single(anonymous.Headers.GetValues("Origin")));
        Assert.Null(anonymous.Headers.Authorization);
        Assert.Equal("Bearer", authenticated.Headers.Authorization?.Scheme);
        Assert.Equal("token", authenticated.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Connect_GoesToTheConfiguredAddress_KeepingTheHostName()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var target = SmokeTarget.FromSettings(Settings(
            ("CAREFLOW_SMOKE_URL", Url), ("CAREFLOW_SMOKE_ORIGIN", Origin), ("CAREFLOW_SMOKE_CONNECT", $"127.0.0.1:{port}")))!;

        var call = target.Client.GetAsync("demo/v1/status", TestContext.Current.CancellationToken);
        using (await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken))
        {
            // A conexão chegou ao endereço configurado, não ao DNS de demo.careflow.test; fechar encerra o TLS.
        }

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => call);
    }

    [Fact]
    public async Task Connect_ToAClosedPort_Fails()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var target = SmokeTarget.FromSettings(Settings(
            ("CAREFLOW_SMOKE_URL", Url), ("CAREFLOW_SMOKE_ORIGIN", Origin), ("CAREFLOW_SMOKE_CONNECT", $"127.0.0.1:{port}")))!;

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => target.Client.GetAsync("demo/v1/status", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LocalCa_IsLoaded()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=CareFlow teste", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var file = Path.Combine(Path.GetTempPath(), $"careflow-smoke-{Guid.NewGuid():N}.crt");
        File.WriteAllText(file, certificate.ExportCertificatePem());
        try
        {
            using var target = SmokeTarget.FromSettings(Settings(
                ("CAREFLOW_SMOKE_URL", Url), ("CAREFLOW_SMOKE_ORIGIN", Origin), ("CAREFLOW_SMOKE_CA", file)));

            Assert.NotNull(target);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static Func<string, string?> Settings(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }
}
