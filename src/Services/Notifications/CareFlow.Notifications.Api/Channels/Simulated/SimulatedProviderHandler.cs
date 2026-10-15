using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Domain.Channels;

using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Channels.Simulated;

/// <summary>
/// Provedor simulado de um canal, no lugar do handler de rede do HttpClient. Fala o mesmo HTTP que um adaptador
/// real falaria (POST de mensagem com <c>Idempotency-Key</c>, consulta por chave), então todo o pipeline do
/// Microsoft.Extensions.Http.Resilience roda de verdade acima dele. Nenhum dado sai do processo.
/// </summary>
internal sealed class SimulatedProviderHandler(
    Channel channel,
    SimulatedProviderLedger ledger,
    IOptionsMonitor<FaultInjectionOptions> faults,
    TimeProvider time) : HttpMessageHandler
{
    public const int MaxTextLength = 1600;

    private const string MessagesPath = "/messages";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (request.Method == HttpMethod.Post && path == MessagesPath)
        {
            return await AcceptAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.Method == HttpMethod.Get && path.StartsWith(MessagesPath + "/", StringComparison.Ordinal))
        {
            var key = Uri.UnescapeDataString(path[(MessagesPath.Length + 1)..]);
            var found = ledger.Find(channel, key);
            return found is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(HttpStatusCode.OK, found);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> AcceptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValues(ProviderRequest.IdempotencyHeader, out var keys)
            || keys.SingleOrDefault() is not { Length: > 0 and <= 128 } key
            || request.Content is null)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        ProviderRequest? body;
        try
        {
            // Lê do buffer, não do stream: a retentativa reenvia o mesmo HttpContent, e o stream de leitura em cache
            // já teria sido descartado na primeira leitura.
            var payload = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            body = JsonSerializer.Deserialize<ProviderRequest>(payload, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        if (body is null || string.IsNullOrWhiteSpace(body.To) || string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > MaxTextLength)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var (message, created) = ledger.Accept(channel, key, body.To, body.Text, time.GetUtcNow());

        // Resposta perdida depois do aceite: só na primeira vez, para a retentativa receber o replay.
        var fault = faults.CurrentValue.For(channel);
        if (created && fault?.Mode == FaultMode.LostResponse && Roll(fault.InjectionRate))
        {
            throw new HttpRequestException("A resposta do provedor simulado se perdeu depois do aceite.");
        }

        return Json(created ? HttpStatusCode.Accepted : HttpStatusCode.OK, message);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, SimulatedMessage message) =>
        new(status) { Content = JsonContent.Create(new ProviderResponse(message.ProviderMessageId, "accepted"), options: JsonSerializerOptions.Web) };

    private static bool Roll(double rate) => rate >= 1.0 || RandomNumberGenerator.GetInt32(1_000_000) < rate * 1_000_000;
}
