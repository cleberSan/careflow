using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Contracts.Appointments;
using CareFlow.Notifications.Api.Common.Observability;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Notifications;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Notifications.Api.Features.Appointments;

/// <summary>
/// Consulta cancelada: descarta a confirmação e o lembrete que ainda não saíram e agenda o aviso de
/// cancelamento, inclusive quando o motivo é a revogação do consentimento para agendamento. Se este evento
/// chegar antes da reserva, a consulta fica marcada como cancelada e a reserva atrasada é ignorada.
/// </summary>
internal sealed class AppointmentCancelledConsumer(NotificationsDbContext db, TimeProvider time, NotificationsMetrics metrics)
    : IIntegrationEventConsumer<AppointmentCancelled>
{
    public async Task HandleAsync(AppointmentCancelled @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        // Eliminação a pedido do titular: os avisos pendentes caem e nenhum aviso de cancelamento sai, porque o
        // contato do paciente deixa de existir no cofre.
        CancellationCause? cause = @event.Reason switch
        {
            AppointmentCancellationReasons.Requested => CancellationCause.Requested,
            AppointmentCancellationReasons.ConsentRevoked => CancellationCause.ConsentRevoked,
            AppointmentCancellationReasons.SubjectErasure => null,
            _ => throw new PoisonMessageException("Notificacoes.MotivoDeCancelamentoDesconhecido"),
        };

        var now = time.GetUtcNow();
        var appointment = await ProjectionStore.LockAppointmentAsync(
            db, context.TenantId, @event.AppointmentId, @event.PatientRef, @event.StartsAt, now, cancellationToken).ConfigureAwait(false);
        if (!appointment.ApplyCancelled(now))
        {
            return;
        }

        var superseded = await db.Notifications
            .Where(n => n.AppointmentId == @event.AppointmentId && n.Status == NotificationStatus.Pending && n.Kind != NotificationKind.Cancellation)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var notification in superseded)
        {
            notification.Supersede(now);
            metrics.ClosedWithoutSending("superseded");
        }

        if (superseded.Count > 0)
        {
            var ids = superseded.Select(n => n.Id).ToArray();
            await db.Jobs.Where(j => ids.Contains(j.NotificationId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (cause is not null && appointment.StartsAt > now)
        {
            db.Schedule(
                Notification.Cancellation(context.TenantId, appointment.AppointmentId, appointment.PatientRef, appointment.StartsAt, cause.Value, now),
                metrics);
        }
    }
}
