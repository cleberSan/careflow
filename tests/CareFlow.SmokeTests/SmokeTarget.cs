using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace CareFlow.SmokeTests;

/// <summary>
/// Para onde o teste aponta. <c>CAREFLOW_SMOKE_URL</c> é o backend público (https://...duckdns.org) e
/// <c>CAREFLOW_SMOKE_ORIGIN</c> a origem do site. Na validação local da stack de produção, <c>CAREFLOW_SMOKE_CONNECT</c>
/// manda a conexão para outro endereço (127.0.0.1:8443) sem mudar o nome do TLS, e <c>CAREFLOW_SMOKE_CA</c> confia
/// numa CA local (a do Caddy com <c>tls internal</c>). Em produção os dois ficam vazios: vale a cadeia pública.
/// </summary>
internal sealed class SmokeTarget : IDisposable
{
    public const string DemoHeader = "X-CareFlow-Demo";
    public const string ForeignOrigin = "https://exemplo-malicioso.invalid";

    private SmokeTarget(Uri baseAddress, string origin, string? connectTo, string? caFile)
    {
        BaseAddress = baseAddress;
        Origin = origin;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        if (connectTo is { Length: > 0 })
        {
            var endpoint = DnsEndPoint(connectTo);
            handler.UseProxy = false;
            handler.ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        if (caFile is { Length: > 0 })
        {
            // Só a CA local vale como raiz; nome e validade continuam conferidos pela validação padrão.
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            policy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificateFromFile(caFile));
            handler.SslOptions.CertificateChainPolicy = policy;
        }

        Client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }

    public Uri BaseAddress { get; }

    public string Origin { get; }

    public HttpClient Client { get; }

    /// <summary>O alvo configurado, ou nada (e o teste é pulado) sem <c>CAREFLOW_SMOKE_URL</c>.</summary>
    public static SmokeTarget? FromEnvironment() => FromSettings(Environment.GetEnvironmentVariable);

    /// <summary>Lê a configuração por <paramref name="read"/>, para os testes da própria leitura não mexerem no ambiente.</summary>
    internal static SmokeTarget? FromSettings(Func<string, string?> read)
    {
        var url = read("CAREFLOW_SMOKE_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var baseAddress = new Uri(url.TrimEnd('/') + "/", UriKind.Absolute);
        if (baseAddress.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("CAREFLOW_SMOKE_URL precisa ser https.");
        }

        var origin = read("CAREFLOW_SMOKE_ORIGIN");
        if (string.IsNullOrWhiteSpace(origin))
        {
            throw new InvalidOperationException("Defina CAREFLOW_SMOKE_ORIGIN com a origem do site.");
        }

        return new SmokeTarget(
            baseAddress,
            origin.TrimEnd('/'),
            read("CAREFLOW_SMOKE_CONNECT"),
            read("CAREFLOW_SMOKE_CA"));
    }

    public static SmokeTarget Require()
    {
        var target = FromEnvironment();
        Assert.SkipWhen(target is null, "CAREFLOW_SMOKE_URL não definida: o teste de fumaça só roda depois de um deploy.");
        return target!;
    }

    /// <summary>Uma chamada como a do site: com o cabeçalho do Painel e a origem do site.</summary>
    public HttpRequestMessage FromSite(HttpMethod method, string path, string? token = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(DemoHeader, "1");
        request.Headers.Add("Origin", Origin);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    public void Dispose() => Client.Dispose();

    private static DnsEndPoint DnsEndPoint(string value)
    {
        var separator = value.LastIndexOf(':');
        return separator > 0 && int.TryParse(value[(separator + 1)..], out var port)
            ? new DnsEndPoint(value[..separator], port)
            : throw new InvalidOperationException("CAREFLOW_SMOKE_CONNECT precisa ser host:porta.");
    }
}
