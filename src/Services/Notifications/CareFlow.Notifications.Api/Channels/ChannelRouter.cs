using System.Diagnostics;

using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Api.Common.Observability;
using CareFlow.Notifications.Api.Contacts;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Notifications;

namespace CareFlow.Notifications.Api.Channels;

internal sealed record RoutingResult(IReadOnlyList<DeliveryAttempt> Attempts, Channel? DeliveredVia, string? ProviderMessageId)
{
    public bool Delivered => DeliveredVia is not null;
}

/// <summary>
/// Fallback entre canais: tenta na ordem da clínica e para no primeiro que entrega. Um canal com circuito
/// aberto é pulado na hora, sem esperar timeout; um canal sem o contato que ele usa também.
/// </summary>
internal sealed class ChannelRouter(ChannelGateway gateway, NotificationsMetrics metrics, TimeProvider time)
{
    public Task<RoutingResult> SendAsync(
        Notification notification,
        PatientContact contact,
        string text,
        ChannelOrder order,
        int round,
        CancellationToken cancellationToken) =>
        SendAsync(notification, contact, text, order, round, plan: null, cancellationToken);

    /// <inheritdoc cref="SendAsync(Notification, PatientContact, string, ChannelOrder, int, CancellationToken)"/>
    public async Task<RoutingResult> SendAsync(
        Notification notification,
        PatientContact contact,
        string text,
        ChannelOrder order,
        int round,
        ClinicChannelPlan? plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(order);

        var attempts = new List<DeliveryAttempt>(order.Channels.Count);
        Channel? failed = null;
        foreach (var channel in order.Channels)
        {
            var address = contact.AddressFor(channel);
            if (address is null)
            {
                attempts.Add(DeliveryAttempt.Record(notification, round, channel, AttemptOutcome.NoContact, error: null, time.GetUtcNow()));
                continue;
            }

            if (failed is not null)
            {
                metrics.Fallback(failed.Value, channel);
                Activity.Current?.AddEvent(new ActivityEvent(
                    "fallback",
                    tags: new ActivityTagsCollection { ["from"] = failed.Value.Slug(), ["to"] = channel.Slug() }));
            }

            var result = await gateway.SendAsync(
                channel, notification.IdempotencyKeyFor(channel), address, text, plan?.CallFor(channel), cancellationToken).ConfigureAwait(false);
            if (result.Kind == ChannelResultKind.Delivered)
            {
                attempts.Add(DeliveryAttempt.Record(notification, round, channel, AttemptOutcome.Delivered, error: null, time.GetUtcNow()));
                return new RoutingResult(attempts, channel, result.ProviderMessageId);
            }

            var outcome = result.Kind == ChannelResultKind.CircuitOpen ? AttemptOutcome.CircuitOpen : AttemptOutcome.Failed;
            attempts.Add(DeliveryAttempt.Record(notification, round, channel, outcome, result.Error, time.GetUtcNow()));
            metrics.ChannelFailed(channel, outcome);
            failed = channel;
        }

        return new RoutingResult(attempts, null, null);
    }
}
