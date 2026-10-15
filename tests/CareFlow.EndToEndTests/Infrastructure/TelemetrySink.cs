using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CareFlow.EndToEndTests.Infrastructure;

/// <summary>
/// O que os serviços exportariam por OTLP: spans, logs e métricas, registrados depois dos processadores de
/// redação de cada serviço (é a mesma cadeia do exportador de verdade). Guarda cópias imutáveis em texto, para
/// varrer atrás de dados pessoais e para montar o trace de um fluxo.
/// </summary>
/// <remarks>
/// Os cinco serviços rodam num processo só, e um <c>TracerProvider</c> escuta todas as fontes do processo:
/// cada span chega aqui uma vez por serviço. Para montar traces, use <see cref="Trace"/>, que desduplica por span.
/// </remarks>
public sealed class TelemetrySink
{
    private readonly ConcurrentQueue<ExportedSpan> _spans = new();
    private readonly ConcurrentQueue<ExportedLog> _logs = new();
    private readonly ConcurrentQueue<ExportedMetric> _metrics = new();
    private readonly ConcurrentBag<MeterProvider> _meterProviders = [];

    public IReadOnlyCollection<ExportedSpan> Spans => _spans;

    public IReadOnlyCollection<ExportedLog> Logs => _logs;

    public IReadOnlyCollection<ExportedMetric> Metrics => _metrics;

    /// <summary>Liga os exportadores de teste no serviço, depois dos que o próprio serviço registrou.</summary>
    public void Attach(IServiceCollection services)
    {
        services.ConfigureOpenTelemetryTracerProvider(tracing =>
            tracing.AddProcessor(new SimpleActivityExportProcessor(new SpanExporter(this))));
        services.ConfigureOpenTelemetryLoggerProvider(logging =>
            logging.AddProcessor(new SimpleLogRecordExportProcessor(new LogExporter(this))));
        services.ConfigureOpenTelemetryMeterProvider(metrics =>
            metrics.AddReader(new PeriodicExportingMetricReader(new MetricExporter(this), exportIntervalMilliseconds: 1_000)));
    }

    public void Track(MeterProvider? provider)
    {
        if (provider is not null)
        {
            _meterProviders.Add(provider);
        }
    }

    /// <summary>Força a exportação das métricas acumuladas até agora.</summary>
    public void FlushMetrics()
    {
        foreach (var provider in _meterProviders)
        {
            provider.ForceFlush(5_000);
        }
    }

    /// <summary>Spans de um trace, um por span (sem as cópias de cada serviço), em ordem de início.</summary>
    public IReadOnlyList<ExportedSpan> Trace(string traceId) =>
        _spans.Where(span => span.TraceId == traceId)
            .GroupBy(span => span.SpanId)
            .Select(group => group.First())
            .OrderBy(span => span.StartedAt)
            .ToArray();

    /// <summary>Tudo o que foi exportado, como texto, para buscas amplas.</summary>
    public IEnumerable<(string Kind, string Content)> Everything() =>
        _spans.Select(span => ("span", span.Content))
            .Concat(_logs.Select(log => ("log", log.Content)))
            .Concat(_metrics.Select(metric => ("metric", metric.Content)));

    private static string ServiceOf(BaseProvider? provider) =>
        provider?.GetResource().Attributes.FirstOrDefault(a => a.Key == "service.name").Value?.ToString() ?? "?";

    private sealed class SpanExporter(TelemetrySink sink) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            var service = ServiceOf(ParentProvider);
            foreach (var activity in batch)
            {
                sink._spans.Enqueue(ExportedSpan.From(service, activity));
            }

            return ExportResult.Success;
        }
    }

    private sealed class LogExporter(TelemetrySink sink) : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            var service = ServiceOf(ParentProvider);
            foreach (var record in batch)
            {
                sink._logs.Enqueue(ExportedLog.From(service, record));
            }

            return ExportResult.Success;
        }
    }

    private sealed class MetricExporter(TelemetrySink sink) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            var service = ServiceOf(ParentProvider);
            foreach (var metric in batch)
            {
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new StringBuilder();
                    foreach (var tag in point.Tags)
                    {
                        tags.Append(tag.Key).Append('=').Append(tag.Value).Append(';');
                    }

                    sink._metrics.Enqueue(new ExportedMetric(service, metric.Name, tags.ToString()));
                }
            }

            return ExportResult.Success;
        }
    }
}

public sealed record ExportedSpan(
    string Service,
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Source,
    string Name,
    ActivityKind Kind,
    DateTime StartedAt,
    IReadOnlyDictionary<string, string?> Tags,
    IReadOnlyList<string> Events,
    ActivityStatusCode Status,
    string Content)
{
    public string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value : null;

    public static ExportedSpan From(string service, Activity activity)
    {
        var tags = activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString(), StringComparer.Ordinal);
        var events = activity.Events
            .Select(e => e.Name + "{" + string.Join(';', e.Tags.Select(tag => $"{tag.Key}={tag.Value}")) + "}")
            .ToArray();
        var parent = activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString();

        // Tudo que o OTLP levaria: nome, atributos, eventos, links, status, baggage e tracestate.
        var content = string.Join(
            '|',
            activity.DisplayName,
            string.Join(';', tags.Select(tag => $"{tag.Key}={tag.Value}")),
            string.Join(';', events),
            string.Join(';', activity.Links.Select(link => link.Context.TraceId.ToHexString())),
            activity.StatusDescription,
            string.Join(';', activity.Baggage.Select(item => $"{item.Key}={item.Value}")),
            activity.TraceStateString);

        return new ExportedSpan(
            service,
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            parent,
            activity.Source.Name,
            activity.DisplayName,
            activity.Kind,
            activity.StartTimeUtc,
            tags,
            events,
            activity.Status,
            content);
    }

    public override string ToString() => $"{Service}:{Name} ({Source}, {Kind}) {SpanId}<-{ParentSpanId}";
}

public sealed record ExportedLog(string Service, string Category, string TraceId, string Content)
{
    public static ExportedLog From(string service, LogRecord record)
    {
        var attributes = record.Attributes is null
            ? string.Empty
            : string.Join(';', record.Attributes.Select(attribute => $"{attribute.Key}={attribute.Value}"));
        var content = string.Join(
            '|',
            record.CategoryName,
            record.FormattedMessage,
            record.Body,
            attributes,
            record.Exception?.ToString());
        return new ExportedLog(service, record.CategoryName ?? string.Empty, record.TraceId.ToHexString(), content);
    }
}

public sealed record ExportedMetric(string Service, string Name, string Tags)
{
    public string Content => $"{Name}|{Tags}";
}
