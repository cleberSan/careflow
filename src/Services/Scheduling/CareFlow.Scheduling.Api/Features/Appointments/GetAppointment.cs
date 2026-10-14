using CareFlow.BuildingBlocks.AspNetCore.Http;
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
        CancellationToken cancellationToken)
    {
        // O filtro de tenant devolve 404 também para consultas de outra clínica, sem revelar que existem (IDOR).
        var appointment = await db.Appointments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return appointment is null
            ? AppointmentErrors.NotFound.ToProblem()
            : TypedResults.Ok(AppointmentResponse.From(appointment));
    }
}
