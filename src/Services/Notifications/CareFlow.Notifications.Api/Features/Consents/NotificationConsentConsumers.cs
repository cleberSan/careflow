using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Contracts.Consents;
using CareFlow.Notifications.Api.Infrastructure.Persistence;

namespace CareFlow.Notifications.Api.Features.Consents;

/// <summary>Concessão de consentimento. Só a finalidade "notificações" interessa; versões já conhecidas são ignoradas.</summary>
internal sealed class ConsentGrantedConsumer(NotificationsDbContext db, TimeProvider time) : IIntegrationEventConsumer<ConsentGranted>
{
    public async Task HandleAsync(ConsentGranted @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(@event.Purpose, ConsentPurposes.Notifications, StringComparison.Ordinal))
        {
            return;
        }

        var now = time.GetUtcNow();
        var consent = await ProjectionStore.LockConsentAsync(db, context.TenantId, @event.PatientRef, @event.Version, now, cancellationToken).ConfigureAwait(false);
        consent.ApplyGranted(@event.Version, now);
    }
}

/// <summary>
/// Revogação do consentimento de notificações. Os avisos pendentes não são apagados: na hora do envio, o
/// despachante encontra a revogação e registra cada um como não enviado, com o motivo na trilha de auditoria.
/// </summary>
internal sealed class ConsentRevokedConsumer(NotificationsDbContext db, TimeProvider time) : IIntegrationEventConsumer<ConsentRevoked>
{
    public async Task HandleAsync(ConsentRevoked @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(@event.Purpose, ConsentPurposes.Notifications, StringComparison.Ordinal))
        {
            return;
        }

        var now = time.GetUtcNow();
        var consent = await ProjectionStore.LockConsentAsync(db, context.TenantId, @event.PatientRef, @event.Version, now, cancellationToken).ConfigureAwait(false);
        consent.ApplyRevoked(@event.Version, now);
    }
}
