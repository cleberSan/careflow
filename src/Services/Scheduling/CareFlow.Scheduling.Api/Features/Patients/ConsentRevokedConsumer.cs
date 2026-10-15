using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Contracts.Consents;
using CareFlow.Scheduling.Api.Common;
using CareFlow.Scheduling.Api.Common.Observability;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.Domain.Appointments;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Scheduling.Api.Features.Patients;

/// <summary>
/// Revogação do consentimento para agendamento: bloqueia novas reservas e cancela as consultas futuras do
/// paciente, cada uma com registro na trilha de auditoria. Tudo na transação do Inbox, então ou a revogação
/// é aplicada inteira, ou a mensagem volta para a fila e é reprocessada.
/// </summary>
internal sealed partial class ConsentRevokedConsumer(
    SchedulingDbContext db,
    TimeProvider time,
    SchedulingMetrics metrics,
    ILogger<ConsentRevokedConsumer> logger)
    : IIntegrationEventConsumer<ConsentRevoked>
{
    public const string CancelAction = "appointment.cancelled.consent-revoked";

    public async Task HandleAsync(ConsentRevoked @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(@event.Purpose, ConsentPurposes.Scheduling, StringComparison.Ordinal))
        {
            return;
        }

        PatientStatusStore.EnsureValidVersion(@event.Version);
        var now = time.GetUtcNow();

        // Trava a projeção antes de olhar as consultas: uma reserva em andamento termina antes (e é
        // cancelada aqui) ou espera e encontra o paciente revogado.
        var patient = await PatientStatusStore.LockAsync(db, context.TenantId, @event.PatientRef, now, cancellationToken).ConfigureAwait(false);
        if (!patient.ApplyConsentRevoked(@event.Version, now))
        {
            // Revogação antiga ou repetida: uma concessão mais nova já vale.
            return;
        }

        var upcoming = await db.Appointments
            .Where(a => a.PatientRef == @event.PatientRef && a.Status == AppointmentStatus.Booked && a.StartsAt > now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cancelled = 0;
        foreach (var appointment in upcoming)
        {
            if (appointment.CancelForConsentRevocation(now).IsSuccess)
            {
                cancelled++;
                db.RecordAccess(
                    context.TenantId, SchedulingAudit.SourceService, SchedulingAudit.SystemActor, CancelAction, SchedulingAudit.ResourceType, appointment.PatientRef, now);
            }
        }

        if (cancelled > 0)
        {
            metrics.CancelledByRevocation(cancelled);
            LogCancelledByRevocation(logger, cancelled);
        }
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "Revogação de consentimento cancelou {Count} consulta(s) futura(s)")]
    private static partial void LogCancelledByRevocation(ILogger logger, int count);
}
