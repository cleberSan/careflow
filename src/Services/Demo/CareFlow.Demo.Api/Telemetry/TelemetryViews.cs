using System.Text.RegularExpressions;

namespace CareFlow.Demo.Api.Telemetry;

internal sealed record SpanView(
    string SpanId,
    string? ParentSpanId,
    string Service,
    string Name,
    string Kind,
    DateTimeOffset StartedAt,
    double DurationMs,
    string Status,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<SpanEventView> Events);

internal sealed record SpanEventView(string Name, IReadOnlyDictionary<string, string> Attributes);

internal sealed record TraceView(string TraceId, IReadOnlyList<SpanView> Spans);

internal sealed record LogView(DateTimeOffset At, string Service, string Severity, string Message);

/// <summary>Lê o que os serviços exportaram, para o Painel mostrar o trace e os logs de uma ação da sessão.</summary>
internal interface ITelemetryReader
{
    Task<TraceView?> TraceAsync(string traceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LogView>> LogsAsync(string traceId, CancellationToken cancellationToken);
}

/// <summary>
/// Só estes atributos e eventos chegam ao navegador, mesmo já redigidos na origem: o Painel mostra o caminho da
/// requisição, não o conteúdo. Lista de permissão, não de bloqueio.
/// </summary>
internal static partial class TelemetryAllowList
{
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "http.request.method",
        "http.route",
        "http.response.status_code",
        "messaging.system",
        "messaging.operation.type",
        "messaging.destination.name",
        "messaging.consumer.group.name",
        "messaging.rabbitmq.destination.routing_key",
        "db.system.name",
        "db.system",
    };

    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "fallback",
        "confirmed-by-idempotency-key",
        "circuit open",
        "circuit halfopen",
        "circuit closed",
        "circuit isolated",
    };

    private static readonly HashSet<string> EventAttributes = new(StringComparer.Ordinal) { "from", "to", "channel" };

    public static bool IsValidTraceId(string? traceId) => traceId is not null && TraceIdPattern().IsMatch(traceId);

    public static bool AllowsAttribute(string key) =>
        Attributes.Contains(key) || (key.StartsWith("careflow.", StringComparison.Ordinal) && key.Length <= 64);

    public static bool AllowsEvent(string name) => Events.Contains(name);

    public static IReadOnlyDictionary<string, string> FilterEvent(IEnumerable<KeyValuePair<string, string>> attributes) =>
        attributes.Where(attribute => EventAttributes.Contains(attribute.Key))
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Truncate(group.First().Value), StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> Filter(IEnumerable<KeyValuePair<string, string>> attributes) =>
        attributes.Where(attribute => AllowsAttribute(attribute.Key))
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Truncate(group.First().Value), StringComparer.Ordinal);

    private static string Truncate(string value) => value.Length <= 128 ? value : value[..128];

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceIdPattern();
}

/// <summary>Sem Tempo e Loki configurados: o Painel diz que a telemetria não está disponível.</summary>
internal sealed class UnavailableTelemetryReader : ITelemetryReader
{
    public Task<TraceView?> TraceAsync(string traceId, CancellationToken cancellationToken) => Task.FromResult<TraceView?>(null);

    public Task<IReadOnlyList<LogView>> LogsAsync(string traceId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LogView>>([]);
}
