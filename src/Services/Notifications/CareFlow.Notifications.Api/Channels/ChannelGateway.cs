using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CareFlow.Notifications.Domain.Channels;

using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CareFlow.Notifications.Api.Channels;

internal enum ChannelResultKind
{
    Delivered = 1,

    /// <summary>Falhou depois das retentativas do pipeline (erro, timeout, rede).</summary>
    Failed,

    /// <summary>Circuito aberto: o pipeline recusou sem chamar o provedor.</summary>
    CircuitOpen,
}

internal sealed record ChannelResult(ChannelResultKind Kind, string? ProviderMessageId = null, string? Error = null)
{
    public static ChannelResult Delivered(string providerMessageId) => new(ChannelResultKind.Delivered, providerMessageId);

    public static ChannelResult Failed(string error) => new(ChannelResultKind.Failed, Error: error);

    public static ChannelResult CircuitOpen { get; } = new(ChannelResultKind.CircuitOpen, Error: "circuit-open");
}

/// <summary>
/// Envia por um canal. Cada canal tem um HttpClient nomeado com o próprio pipeline de resiliência (timeout,
/// retry com jitter, circuit breaker e falhas injetáveis). A chave de idempotência vai em todas as tentativas.
/// </summary>
internal sealed partial class ChannelGateway(IHttpClientFactory clients, ILogger<ChannelGateway> logger)
{
    public static string SendClientName(Channel channel) => $"channel-{channel.Slug()}";

    public static string StatusClientName(Channel channel) => $"channel-{channel.Slug()}-status";

    public async Task<ChannelResult> SendAsync(Channel channel, string idempotencyKey, string recipient, string text, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(SendClientName(channel));
        using var request = new HttpRequestMessage(HttpMethod.Post, "messages")
        {
            Content = JsonContent.Create(new ProviderRequest(recipient, text), options: JsonSerializerOptions.Web),
        };
        request.Headers.Add(ProviderRequest.IdempotencyHeader, idempotencyKey);

        string error;
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var accepted = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
                if (accepted is not null)
                {
                    return ChannelResult.Delivered(accepted.MessageId);
                }

                error = "invalid-response";
            }
            else
            {
                error = $"http-{(int)response.StatusCode}";
            }
        }
        catch (BrokenCircuitException)
        {
            LogCircuitOpen(logger, channel);
            return ChannelResult.CircuitOpen;
        }
        catch (Exception ex) when (IsTransportFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            error = ex.GetType().Name;
        }

        // O provedor pode ter aceitado e a resposta se perdido. Antes de cair para outro canal (o que mandaria
        // uma segunda mensagem ao paciente), pergunta ao provedor por esta chave.
        var confirmed = await LookupAsync(channel, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (confirmed is not null)
        {
            LogConfirmedAfterFailure(logger, channel, error);
            return ChannelResult.Delivered(confirmed.MessageId);
        }

        LogFailed(logger, channel, error);
        return ChannelResult.Failed(error);
    }

    private async Task<ProviderResponse?> LookupAsync(Channel channel, string idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            var client = clients.CreateClient(StatusClientName(channel));
            using var response = await client.GetAsync(new Uri("messages/" + Uri.EscapeDataString(idempotencyKey), UriKind.Relative), cancellationToken)
                .ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK ? await ReadAsync(response, cancellationToken).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (IsTransportFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            // Sem confirmação: segue como falha. Um provedor fora do ar para consulta e com a mensagem já aceita é
            // o único caso em que o paciente pode receber o aviso por dois canais (ADR-0009).
            return null;
        }
    }

    private static async Task<ProviderResponse?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ProviderResponse>(JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
            return body is { MessageId.Length: > 0 and <= 128 } ? body : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException or TaskCanceledException or OperationCanceledException;

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Canal {Channel} falhou depois das retentativas: {Error}")]
    private static partial void LogFailed(ILogger logger, Channel channel, string error);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Canal {Channel} com circuito aberto; tentando o próximo canal")]
    private static partial void LogCircuitOpen(ILogger logger, Channel channel);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information, Message = "Canal {Channel} confirmou a entrega pela chave de idempotência depois de {Error}")]
    private static partial void LogConfirmedAfterFailure(ILogger logger, Channel channel, string error);
}
