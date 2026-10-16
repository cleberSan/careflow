extern alias demo;

using System.Diagnostics;

using CareFlow.Testing;

using demo::CareFlow.Demo.Api.Services;
using demo::CareFlow.Demo.Api.Telemetry;

namespace CareFlow.EndToEndTests.Infrastructure;

/// <summary>O Demo se identifica aos serviços pelos cabeçalhos de teste, com a mesma clínica e os mesmos escopos.</summary>
internal sealed class TestHeadersAuthenticator : IServiceAuthenticator
{
    public void Authenticate(HttpRequestMessage request, ServiceCall call)
    {
        request.Headers.Remove(TestAuthHandler.TenantHeader);
        request.Headers.Remove(TestAuthHandler.ScopesHeader);

        // Chamada anônima (o download pelo link): sai sem identidade, como no Demo de verdade.
        if (call.Scopes.Count == 0)
        {
            return;
        }

        request.Headers.Add(TestAuthHandler.TenantHeader, call.Clinic.ToString());
        request.Headers.Add(TestAuthHandler.ScopesHeader, string.Join(' ', call.Scopes));
    }

    public void Forget(Guid clinic)
    {
    }
}

/// <summary>Traces e logs do que os serviços exportaram neste processo, no lugar do Tempo e do Loki.</summary>
internal sealed class SinkTelemetryReader(TelemetrySink sink) : ITelemetryReader
{
    public Task<TraceView?> TraceAsync(string traceId, CancellationToken cancellationToken)
    {
        var spans = sink.Trace(traceId);
        if (spans.Count == 0)
        {
            return Task.FromResult<TraceView?>(null);
        }

        var views = spans.Select(span => new SpanView(
                span.SpanId,
                span.ParentSpanId,
                span.Service,
                span.Name,
                span.Kind.ToString().ToLowerInvariant(),
                new DateTimeOffset(span.StartedAt, TimeSpan.Zero),
                span.Duration.TotalMilliseconds,
                span.Status switch { ActivityStatusCode.Ok => "ok", ActivityStatusCode.Error => "error", _ => "unset" },
                TelemetryAllowList.Filter(span.Tags.Where(tag => tag.Value is not null).Select(tag => new KeyValuePair<string, string>(tag.Key, tag.Value!))),
                [.. span.EventDetails
                    .Where(e => TelemetryAllowList.AllowsEvent(e.Name))
                    .Select(e => new SpanEventView(e.Name, TelemetryAllowList.FilterEvent(e.Attributes)))]))
            .ToArray();
        return Task.FromResult<TraceView?>(new TraceView(traceId, views));
    }

    public Task<IReadOnlyList<LogView>> LogsAsync(string traceId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LogView>>(
            [.. sink.LogsOf(traceId).Select(log => new LogView(new DateTimeOffset(log.Timestamp, TimeSpan.Zero), log.Service, log.Severity, log.Message))]);
}
