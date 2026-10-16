using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Messaging.Rights;
using CareFlow.Contracts.Rights;
using CareFlow.Scheduling.Api.Common;
using CareFlow.Scheduling.Api.Features.Appointments;
using CareFlow.Scheduling.Api.Features.Patients;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.Domain.Appointments;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Scheduling.Api.Features.Rights;

/// <summary>
/// Resposta da agenda a um pedido do titular (ADR-0012). Exportação: todas as consultas do paciente. Eliminação:
/// cancela as futuras (sem aviso ao paciente), mantém as passadas pelo prazo da política e bloqueia novas reservas.
/// </summary>
internal sealed class SubjectRequestOpenedConsumer(SchedulingDbContext db, SchedulingEraser eraser, TimeProvider time)
    : IIntegrationEventConsumer<SubjectRequestOpened>
{
    public const string ExportAction = "appointment.exported";

    public async Task HandleAsync(SubjectRequestOpened @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);
        var now = time.GetUtcNow();

        if (@event.Kind == SubjectRequestKinds.Erasure)
        {
            var lines = await eraser.EraseAsync(context.TenantId, @event.PatientRef, now, cancellationToken).ConfigureAwait(false);
            db.AnswerErasure(context.TenantId, @event.RequestId, SubjectRequestServices.Scheduling, lines, now);
            return;
        }

        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => a.PatientRef == @event.PatientRef)
            .OrderBy(a => a.StartsAt)
            .Select(a => new SchedulingSection.AppointmentItem(
                a.Id, a.ProfessionalId, a.StartsAt, a.EndsAt, a.Status.ToString(), a.CreatedAt, a.CancelledAt,
                a.CancellationReason == null ? null : a.CancellationReason.ToString()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        db.AnswerExport(context.TenantId, @event, SubjectRequestServices.Scheduling, new SchedulingSection(appointments), now);
        db.RecordAccess(context.TenantId, SchedulingAudit.SourceService, SchedulingAudit.RightsActor, ExportAction, SchedulingAudit.ResourceType, @event.PatientRef, now);
    }
}

/// <summary>Reaplica a eliminação depois de restaurar um backup. Idempotente.</summary>
internal sealed class ErasureReappliedConsumer(SchedulingEraser eraser, TimeProvider time) : IIntegrationEventConsumer<ErasureReapplied>
{
    public async Task HandleAsync(ErasureReapplied @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);
        await eraser.EraseAsync(context.TenantId, @event.PatientRef, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }
}

internal sealed partial class SchedulingEraser(SchedulingDbContext db, ILogger<SchedulingEraser> logger)
{
    public const string CancelAction = "appointment.cancelled.subject-erasure";
    public const string ErasedAction = "patient.erased";

    public async Task<IReadOnlyList<ErasureLine>> EraseAsync(Guid tenantId, Guid patientRef, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Trava a projeção primeiro, como a revogação: uma reserva em andamento termina antes (e é cancelada aqui)
        // ou espera e encontra o paciente eliminado.
        var patient = await PatientStatusStore.LockAsync(db, tenantId, patientRef, now, cancellationToken).ConfigureAwait(false);
        var newlyErased = patient.MarkErased(now);

        var upcoming = await db.Appointments
            .Where(a => a.PatientRef == patientRef && a.Status == AppointmentStatus.Booked && a.StartsAt > now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cancelled = 0;
        foreach (var appointment in upcoming)
        {
            if (appointment.CancelForSubjectErasure(now).IsSuccess)
            {
                cancelled++;
                db.RecordAccess(tenantId, SchedulingAudit.SourceService, SchedulingAudit.RightsActor, CancelAction, SchedulingAudit.ResourceType, patientRef, now);
                db.PublishCancelled(appointment, now);
            }
        }

        var retained = await db.Appointments
            .CountAsync(a => a.PatientRef == patientRef && !(a.Status == AppointmentStatus.Booked && a.StartsAt > now), cancellationToken)
            .ConfigureAwait(false);

        if (newlyErased)
        {
            db.RecordAccess(tenantId, SchedulingAudit.SourceService, SchedulingAudit.RightsActor, ErasedAction, SchedulingAudit.ResourceType, patientRef, now);
        }

        LogErased(logger, cancelled, retained);
        return
        [
            new(RetentionPolicy.Items.SchedulingFutureAppointments, RetentionActions.Cancelled, cancelled),
            new(RetentionPolicy.Items.SchedulingPastAppointments, RetentionActions.Retained, retained),
        ];
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "Eliminação do titular na agenda: {Cancelled} consulta(s) futura(s) cancelada(s), {Retained} mantida(s)")]
    private static partial void LogErased(ILogger logger, int cancelled, int retained);
}
