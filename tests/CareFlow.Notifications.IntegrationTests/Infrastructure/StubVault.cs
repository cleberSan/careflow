using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace CareFlow.Notifications.IntegrationTests.Infrastructure;

/// <summary>
/// Faz o papel do cofre para o cliente real de contatos (com o pipeline de resiliência): responde em sequência o
/// que o teste programar e guarda as requisições recebidas.
/// </summary>
internal sealed class StubVault : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    /// <summary>Chave do emissor interno; a fixture descarta (a fábrica de HttpClient pode descartar o handler antes).</summary>
    public ECDsa SigningKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PrivateKeyBase64 => Convert.ToBase64String(SigningKey.ExportPkcs8PrivateKey());

    public ConcurrentQueue<(string Path, string? Authorization)> Requests { get; } = new();

    public void Respond(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        _responses.Clear();
        Requests.Clear();
        foreach (var response in responses)
        {
            _responses.Enqueue(response);
        }
    }

    public static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode status) => _ => new HttpResponseMessage(status);

    public static Func<HttpRequestMessage, HttpResponseMessage> Contact(string json) =>
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    public static Func<HttpRequestMessage, HttpResponseMessage> Throw() => _ => throw new HttpRequestException("cofre fora do ar (simulado)");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
        var responder = _responses.TryDequeue(out var next) ? next : Status(HttpStatusCode.InternalServerError);
        return Task.FromResult(responder(request));
    }
}
