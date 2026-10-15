using System.Globalization;
using System.Net;
using System.Text.Json;

namespace CareFlow.Demo.Api.Telemetry;

/// <summary>
/// Lê traces do Tempo (<c>/api/v2/traces/{id}</c>, OTLP em JSON) e logs do Loki (filtro por <c>trace_id</c>).
/// O endereço é fixo da configuração e o id do trace é validado como 32 dígitos hexadecimais antes de entrar no
/// caminho ou na consulta: nada vindo do navegador muda o destino (SSRF) nem a consulta (injeção em LogQL).
/// </summary>
internal sealed class GrafanaStackReader(IHttpClientFactory clients) : ITelemetryReader
{
    public const string TempoClient = "tempo";
    public const string LokiClient = "loki";

    private const int MaxSpans = 500;
    private const int MaxLogs = 200;

    public async Task<TraceView?> TraceAsync(string traceId, CancellationToken cancellationToken)
    {
        if (!TelemetryAllowList.IsValidTraceId(traceId))
        {
            return null;
        }

        using var response = await clients.CreateClient(TempoClient).GetAsync($"api/v2/traces/{traceId}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var spans = OtlpJson.ReadSpans(document.RootElement).Take(MaxSpans).ToArray();
        return spans.Length == 0 ? null : new TraceView(traceId, spans);
    }

    public async Task<IReadOnlyList<LogView>> LogsAsync(string traceId, CancellationToken cancellationToken)
    {
        if (!TelemetryAllowList.IsValidTraceId(traceId))
        {
            return [];
        }

        var query = Uri.EscapeDataString($"{{service_name=~\"careflow-.+\"}} | trace_id=\"{traceId}\"");
        using var response = await clients.CreateClient(LokiClient)
            .GetAsync($"loki/api/v1/query_range?since=1h&direction=forward&limit={MaxLogs}&query={query}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);

        var logs = new List<LogView>();
        if (!document.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("result", out var streams))
        {
            return logs;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            var labels = stream.GetProperty("stream");
            var service = Label(labels, "service_name");
            var severity = Label(labels, "severity_text");
            foreach (var value in stream.GetProperty("values").EnumerateArray())
            {
                var nanos = long.Parse(value[0].GetString()!, CultureInfo.InvariantCulture);
                logs.Add(new LogView(DateTimeOffset.UnixEpoch.AddTicks(nanos / 100), service, severity, Truncate(value[1].GetString() ?? string.Empty)));
            }
        }

        return [.. logs.OrderBy(log => log.At).Take(MaxLogs)];
    }

    private static string Label(JsonElement labels, string name) =>
        labels.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];
}

/// <summary>Leitura de spans em OTLP/JSON como o Tempo devolve (ids em Base64 ou hexadecimal).</summary>
internal static class OtlpJson
{
    public static IEnumerable<SpanView> ReadSpans(JsonElement root)
    {
        var container = root.TryGetProperty("trace", out var trace) ? trace : root;
        if (!container.TryGetProperty("resourceSpans", out var resources) && !container.TryGetProperty("batches", out resources))
        {
            yield break;
        }

        foreach (var resource in resources.EnumerateArray())
        {
            var service = resource.TryGetProperty("resource", out var res) ? Attributes(res).GetValueOrDefault("service.name", "?") : "?";
            var scopes = resource.TryGetProperty("scopeSpans", out var scopeSpans) ? scopeSpans
                : resource.TryGetProperty("instrumentationLibrarySpans", out var legacy) ? legacy : default;
            if (scopes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var scope in scopes.EnumerateArray())
            {
                foreach (var span in scope.GetProperty("spans").EnumerateArray())
                {
                    yield return ReadSpan(service, span);
                }
            }
        }
    }

    private static SpanView ReadSpan(string service, JsonElement span)
    {
        var start = Nanos(span, "startTimeUnixNano");
        var end = Nanos(span, "endTimeUnixNano");
        var events = span.TryGetProperty("events", out var list)
            ? list.EnumerateArray()
                .Select(e => (Name: e.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty, Element: e))
                .Where(e => TelemetryAllowList.AllowsEvent(e.Name))
                .Select(e => new SpanEventView(e.Name, TelemetryAllowList.FilterEvent(Attributes(e.Element))))
                .ToArray()
            : [];

        return new SpanView(
            Id(span, "spanId") ?? string.Empty,
            Id(span, "parentSpanId"),
            service,
            span.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            Kind(span),
            DateTimeOffset.UnixEpoch.AddTicks(start / 100),
            Math.Max(0, end - start) / 1_000_000d,
            Status(span),
            TelemetryAllowList.Filter(Attributes(span)),
            events);
    }

    private static Dictionary<string, string> Attributes(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!element.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            var key = attribute.GetProperty("key").GetString();
            if (key is null || !attribute.TryGetProperty("value", out var value))
            {
                continue;
            }

            foreach (var property in value.EnumerateObject())
            {
                result[key] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
                break;
            }
        }

        return result;
    }

    private static string? Id(JsonElement span, string property)
    {
        if (!span.TryGetProperty(property, out var value) || value.GetString() is not { Length: > 0 } raw)
        {
            return null;
        }

        if (raw.Length is 16 or 32 && raw.All(Uri.IsHexDigit))
        {
            return raw.ToLowerInvariant();
        }

        try
        {
            return Convert.ToHexString(Convert.FromBase64String(raw)).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static long Nanos(JsonElement span, string property) =>
        span.TryGetProperty(property, out var value) && long.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos)
            ? nanos
            : 0;

    private static string Kind(JsonElement span)
    {
        if (!span.TryGetProperty("kind", out var kind))
        {
            return "internal";
        }

        var text = kind.ValueKind == JsonValueKind.Number
            ? kind.GetInt32() switch { 2 => "server", 3 => "client", 4 => "producer", 5 => "consumer", _ => "internal" }
            : kind.GetString() ?? "SPAN_KIND_INTERNAL";
        return text.Replace("SPAN_KIND_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private static string Status(JsonElement span)
    {
        if (!span.TryGetProperty("status", out var status) || !status.TryGetProperty("code", out var code))
        {
            return "unset";
        }

        var text = code.ValueKind == JsonValueKind.Number
            ? code.GetInt32() switch { 1 => "ok", 2 => "error", _ => "unset" }
            : code.GetString() ?? "STATUS_CODE_UNSET";
        return text.Replace("STATUS_CODE_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }
}
