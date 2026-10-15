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

    private readonly Counter<long> _published;
    private readonly Counter<long> _publishFailures;
    private readonly Counter<long> _consumed;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _consumeFailures;
    private readonly Counter<long> _deadLettered;

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
    }

    public void Published(string eventType) => _published.Add(1, Tag(eventType));

    public void PublishFailed(string eventType) => _publishFailures.Add(1, Tag(eventType));

    public void Consumed(string eventType) => _consumed.Add(1, Tag(eventType));

    public void Duplicate(string eventType) => _duplicates.Add(1, Tag(eventType));

    public void ConsumeFailed(string eventType) => _consumeFailures.Add(1, Tag(eventType));

    public void Rejected() => _deadLettered.Add(1);

    private static KeyValuePair<string, object?> Tag(string eventType) => new("event_type", eventType);
}
