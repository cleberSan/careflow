using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Scheduling.Api.Common;
using CareFlow.Scheduling.Api.Common.Http;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.Domain.Appointments;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Scheduling.Api.Features.Appointments;

internal static class GetAppointmentEndpoint
{
    public static async Task<Results<Ok<AppointmentResponse>, ProblemHttpResult>> HandleAsync(
        Guid id,
        SchedulingDbContext db,
        TimeProvider time,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // O filtro de tenant devolve 404 também para consultas de outra clínica, sem revelar que existem (IDOR).
        var appointment = await db.Appointments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (appointment is null)
        {
            return AppointmentErrors.NotFound.ToProblem();
        }

        // A consulta revela que o paciente tem atendimento marcado: a leitura também entra na trilha.
        db.RecordAccess(
            appointment.TenantId,
            SchedulingAudit.SourceService,
            AuditActor.From(httpContext.User),
            "appointment.read",
            SchedulingAudit.ResourceType,
            appointment.PatientRef,
            time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(AppointmentResponse.From(appointment));
    }
}
