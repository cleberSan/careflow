using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CareFlow.BuildingBlocks.Messaging.Diagnostics;

/// <summary>
/// Traces e métricas da mensageria. O <c>traceparent</c> gravado no Outbox liga o span da requisição que
/// gerou o evento ao span de publicação e ao de consumo em outro serviço. Nenhuma tag carrega clínica ou pseudônimo.
/// </summary>
public sealed class MessagingDiagnostics
{
    public const string SourceName = "CareFlow.Messaging";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    // Atraso de propagação: de milissegundos (mesmo processo) a minutos (broker fora do ar).
    private static readonly double[] LagBuckets = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 900];

    private readonly Counter<long> _published;
    private readonly Counter<long> _publishFailures;
    private readonly Counter<long> _consumed;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _consumeFailures;
    private readonly Counter<long> _deadLettered;
    private readonly Histogram<double> _deliveryLag;
    private readonly ConcurrentDictionary<string, long> _queueDepths = new(StringComparer.Ordinal);
    private BacklogReading? _backlog;

    public MessagingDiagnostics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(SourceName);
        _published = meter.CreateCounter<long>("careflow.messaging.outbox.published", "{message}", "Mensagens do Outbox confirmadas pelo broker.");
        _publishFailures = meter.CreateCounter<long>("careflow.messaging.outbox.failures", "{attempt}", "Tentativas de publicação que falharam e serão repetidas.");
        _consumed = meter.CreateCounter<long>("careflow.messaging.inbox.processed", "{message}", "Mensagens processadas pela primeira vez.");
        _duplicates = meter.CreateCounter<long>("careflow.messaging.inbox.duplicates", "{message}", "Mensagens repetidas, ignoradas pelo Inbox.");
        _consumeFailures = meter.CreateCounter<long>("careflow.messaging.inbox.failures", "{attempt}", "Falhas de processamento devolvidas ao broker para nova entrega.");
        _deadLettered = meter.CreateCounter<long>("careflow.messaging.inbox.rejected", "{message}", "Mensagens inválidas enviadas direto para a fila de mensagens mortas.");
        _deliveryLag = meter.CreateHistogram(
            "careflow.messaging.delivery.lag",
            "s",
            "Do evento gravado no Outbox ao efeito confirmado no Inbox do consumidor (inclui a revogação chegando à agenda).",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LagBuckets });

        meter.CreateObservableGauge(
            "careflow.messaging.outbox.pending",
            () => _backlog is { } backlog ? [new Measurement<long>(backlog.Pending)] : Array.Empty<Measurement<long>>(),
            "{message}",
            "Eventos gravados e ainda não confirmados pelo broker.");
        meter.CreateObservableGauge(
            "careflow.messaging.outbox.oldest_pending_age",
            () => _backlog is { } backlog ? [new Measurement<double>(backlog.OldestAge.TotalSeconds)] : Array.Empty<Measurement<double>>(),
            "s",
            "Idade do evento pendente mais antigo no Outbox.");
        meter.CreateObservableGauge(
            "careflow.messaging.queue.depth",
            () => _queueDepths.Select(entry => new Measurement<long>(entry.Value, new KeyValuePair<string, object?>("queue", entry.Key))),
            "{message}",
            "Mensagens prontas na fila do serviço e na fila de mensagens mortas dele.");
    }

    public void Published(string eventType) => _published.Add(1, Tag(eventType));

    public void PublishFailed(string eventType) => _publishFailures.Add(1, Tag(eventType));

    public void Consumed(string eventType) => _consumed.Add(1, Tag(eventType));

    public void Duplicate(string eventType) => _duplicates.Add(1, Tag(eventType));

    public void ConsumeFailed(string eventType) => _consumeFailures.Add(1, Tag(eventType));

    public void Rejected() => _deadLettered.Add(1);

    /// <summary>Atraso negativo (relógios diferentes entre máquinas) conta como zero.</summary>
    public void DeliveryLag(string eventType, string consumer, TimeSpan lag) =>
        _deliveryLag.Record(Math.Max(0, lag.TotalSeconds), Tag(eventType), new KeyValuePair<string, object?>("consumer", consumer));

    public void OutboxBacklog(long pending, TimeSpan oldestAge) => _backlog = new BacklogReading(pending, oldestAge);

    public void QueueDepth(string queue, long depth) => _queueDepths[queue] = depth;

    private static KeyValuePair<string, object?> Tag(string eventType) => new("event_type", eventType);

    private sealed record BacklogReading(long Pending, TimeSpan OldestAge);
}
