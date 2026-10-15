using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
/// O serviço dono de cada span vem do contexto em que ele começou (<see cref="CurrentService"/>), marcado na
/// subida de cada host e em cada requisição que ele atende, como cada processo saberia em produção.
/// </remarks>
public sealed class TelemetrySink
{
    /// <summary>Tag só de teste com o serviço dono do span.</summary>
    public const string ServiceTag = "test.service";

    /// <summary>Serviço em cujo contexto o código está rodando (host em subida, requisição atendida).</summary>
    public static readonly AsyncLocal<string?> CurrentService = new();

    private readonly ConcurrentQueue<ExportedSpan> _spans = new();
    private readonly ConcurrentQueue<ExportedLog> _logs = new();
    private readonly ConcurrentQueue<ExportedMetric> _metrics = new();
    private readonly ConcurrentBag<MeterProvider> _meterProviders = [];

    public IReadOnlyCollection<ExportedSpan> Spans => _spans;

    public IReadOnlyCollection<ExportedLog> Logs => _logs;

    public IReadOnlyCollection<ExportedMetric> Metrics => _metrics;

    /// <summary>Liga os exportadores de teste no serviço, depois dos que o próprio serviço registrou.</summary>
    public void Attach(IServiceCollection services, string serviceName)
    {
        services.AddSingleton<IStartupFilter>(new ServiceAttribution(serviceName));
        services.ConfigureOpenTelemetryTracerProvider(tracing => tracing
            .AddProcessor(new ServiceAttributionProcessor())
            .AddProcessor(new SimpleActivityExportProcessor(new SpanExporter(this))));
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

    /// <summary>Logs exportados de um trace, um por registro (sem repetir), em ordem.</summary>
    public IReadOnlyList<ExportedLog> LogsOf(string traceId) =>
        _logs.Where(log => log.TraceId == traceId).OrderBy(log => log.Timestamp).ToArray();

    private static string ServiceOf(BaseProvider? provider) =>
        provider?.GetResource().Attributes.FirstOrDefault(a => a.Key == "service.name").Value?.ToString() ?? "?";

    /// <summary>Marca o span com o serviço do contexto em que ele começou, se ninguém marcou antes.</summary>
    private sealed class ServiceAttributionProcessor : BaseProcessor<Activity>
    {
        public override void OnStart(Activity data)
        {
            if (CurrentService.Value is { } service && data.GetTagItem(ServiceTag) is null)
            {
                data.SetTag(ServiceTag, service);
            }
        }
    }

    /// <summary>
    /// Primeiro middleware de cada host: o span da requisição já começou no contexto de quem chamou (no mesmo
    /// processo), então é corrigido aqui, e o que a requisição fizer depois fica no contexto deste serviço.
    /// </summary>
    private sealed class ServiceAttribution(string serviceName) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                Activity.Current?.SetTag(ServiceTag, serviceName);
                CurrentService.Value = serviceName;
                await nextMiddleware(context);
            });
            next(app);
        };
    }

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
    TimeSpan Duration,
    IReadOnlyDictionary<string, string?> Tags,
    IReadOnlyList<string> Events,
    IReadOnlyList<ExportedEvent> EventDetails,
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
        var owner = tags.GetValueOrDefault(TelemetrySink.ServiceTag) ?? service;

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
            owner,
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            parent,
            activity.Source.Name,
            activity.DisplayName,
            activity.Kind,
            activity.StartTimeUtc,
            activity.Duration,
            tags,
            events,
            [.. activity.Events.Select(e => new ExportedEvent(
                e.Name,
                e.Tags.Select(tag => new KeyValuePair<string, string>(tag.Key, tag.Value?.ToString() ?? string.Empty)).ToArray()))],
            activity.Status,
            content);
    }

    public override string ToString() => $"{Service}:{Name} ({Source}, {Kind}) {SpanId}<-{ParentSpanId}";
}

public sealed record ExportedEvent(string Name, IReadOnlyList<KeyValuePair<string, string>> Attributes);

public sealed record ExportedLog(string Service, string Category, string TraceId, DateTime Timestamp, string Severity, string Message, string Content)
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
        return new ExportedLog(
            service,
            record.CategoryName ?? string.Empty,
            record.TraceId.ToHexString(),
            record.Timestamp,
            record.LogLevel.ToString(),
            record.FormattedMessage ?? record.Body ?? string.Empty,
            content);
    }
}

public sealed record ExportedMetric(string Service, string Name, string Tags)
{
    public string Content => $"{Name}|{Tags}";
}
