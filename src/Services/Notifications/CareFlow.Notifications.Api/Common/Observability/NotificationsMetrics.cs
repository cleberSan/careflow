using System.Diagnostics.Metrics;

using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Notifications;

namespace CareFlow.Notifications.Api.Common.Observability;

/// <summary>
/// Métricas por canal para o Painel de resiliência: envios, falhas, fallbacks e estado do circuit breaker.
/// Nenhuma tag carrega pseudônimo, clínica ou contato.
/// </summary>
internal sealed class NotificationsMetrics
{
    public const string MeterName = "CareFlow.Notifications";

    private readonly Counter<long> _scheduled;
    private readonly Counter<long> _sent;
    private readonly Counter<long> _channelFailures;
    private readonly Counter<long> _fallbacks;
    private readonly Counter<long> _closedWithoutSending;
    private readonly Counter<long> _circuitTransitions;
    private readonly Counter<long> _contactLookups;

    public NotificationsMetrics(IMeterFactory meterFactory, ChannelCircuits circuits)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(circuits);

        var meter = meterFactory.Create(MeterName);
        _scheduled = meter.CreateCounter<long>("careflow.notifications.scheduled", "{notification}", "Avisos criados a partir de eventos da agenda.");
        _sent = meter.CreateCounter<long>("careflow.notifications.sent", "{notification}", "Avisos aceitos por um provedor, por canal.");
        _channelFailures = meter.CreateCounter<long>("careflow.notifications.channel.failures", "{attempt}", "Tentativas por um canal que não entregaram.");
        _fallbacks = meter.CreateCounter<long>("careflow.notifications.fallbacks", "{fallback}", "Passagens de um canal para o próximo da ordem.");
        _closedWithoutSending = meter.CreateCounter<long>("careflow.notifications.not_sent", "{notification}", "Avisos encerrados sem envio, por motivo.");
        _contactLookups = meter.CreateCounter<long>("careflow.notifications.contact.lookups", "{lookup}", "Leituras de contato no cofre, por resultado.");
        _circuitTransitions = meter.CreateCounter<long>("careflow.notifications.circuit.transitions", "{transition}", "Mudanças de estado do circuit breaker.");

        // 0 = fechado, 1 = meio-aberto, 2 = aberto, 3 = isolado manualmente.
        meter.CreateObservableGauge(
            "careflow.notifications.circuit.state",
            () => circuits.Snapshot().Select(entry =>
                new Measurement<int>((int)entry.Value, new KeyValuePair<string, object?>("channel", entry.Key.Slug()))),
            "{state}",
            "Estado do circuit breaker de cada canal.");

        circuits.StateChanged += (channel, state) =>
            _circuitTransitions.Add(1, Tag("channel", channel.Slug()), Tag("state", state.ToString().ToLowerInvariant()));
    }

    public void Scheduled(NotificationKind kind) => _scheduled.Add(1, Tag("kind", kind.ToString().ToLowerInvariant()));

    public void Sent(Channel channel, NotificationKind kind) =>
        _sent.Add(1, Tag("channel", channel.Slug()), Tag("kind", kind.ToString().ToLowerInvariant()));

    public void ChannelFailed(Channel channel, AttemptOutcome outcome) =>
        _channelFailures.Add(1, Tag("channel", channel.Slug()), Tag("outcome", outcome.ToString().ToLowerInvariant()));

    public void Fallback(Channel from, Channel to) => _fallbacks.Add(1, Tag("from", from.Slug()), Tag("to", to.Slug()));

    public void ContactLookup(string outcome) => _contactLookups.Add(1, Tag("outcome", outcome));

    public void ClosedWithoutSending(string reason) => _closedWithoutSending.Add(1, Tag("reason", reason));

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);
}
