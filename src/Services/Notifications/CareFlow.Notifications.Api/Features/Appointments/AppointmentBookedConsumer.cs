using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Contracts.Appointments;
using CareFlow.Notifications.Api.Common.Observability;
using CareFlow.Notifications.Api.Infrastructure;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Notifications;

using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Features.Appointments;

/// <summary>
/// Consulta reservada: agenda a confirmação (agora) e o lembrete (na antecedência da clínica), na transação do
/// Inbox. Se o cancelamento chegou antes, a reserva é ignorada e nada é agendado.
/// </summary>
internal sealed class AppointmentBookedConsumer(
    NotificationsDbContext db,
    TimeProvider time,
    NotificationsMetrics metrics,
    IOptions<NotificationOptions> options)
    : IIntegrationEventConsumer<AppointmentBooked>
{
    public async Task HandleAsync(AppointmentBooked @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (@event.EndsAt <= @event.StartsAt)
        {
            throw new PoisonMessageException("Notificacoes.HorarioInvalido");
        }

        var now = time.GetUtcNow();
        var appointment = await ProjectionStore.LockAppointmentAsync(
            db, context.TenantId, @event.AppointmentId, @event.PatientRef, @event.StartsAt, now, cancellationToken).ConfigureAwait(false);

        // Reserva repetida, ou já cancelada; e uma consulta que já começou não tem o que confirmar.
        if (!appointment.ApplyBooked(now) || appointment.StartsAt <= now)
        {
            return;
        }

        var clinic = await ProjectionStore.ClinicSettingsAsync(db, context.TenantId, options.Value.DefaultClinicName, cancellationToken).ConfigureAwait(false);
        db.Schedule(Notification.Confirmation(context.TenantId, appointment.AppointmentId, appointment.PatientRef, appointment.StartsAt, now), metrics);

        var reminder = Notification.Reminder(
            context.TenantId, appointment.AppointmentId, appointment.PatientRef, appointment.StartsAt, clinic.ReminderLead, now);
        if (reminder is not null)
        {
            db.Schedule(reminder, metrics);
        }
    }
}

internal static class NotificationScheduling
{
    /// <summary>Grava o aviso e a entrada dele na fila de envios, na mesma transação.</summary>
    public static void Schedule(this NotificationsDbContext db, Notification notification, NotificationsMetrics metrics)
    {
        db.Notifications.Add(notification);
        db.Jobs.Add(NotificationJob.For(notification));
        metrics.Scheduled(notification.Kind);
    }
}
