using CareFlow.BuildingBlocks.Results;
using CareFlow.Scheduling.Api.Common.Http;
using CareFlow.Scheduling.Api.Common.Observability;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.Domain.Appointments;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Scheduling.Api.Features.Appointments;

internal sealed partial class CancelAppointmentHandler(
    SchedulingDbContext db,
    TimeProvider time,
    SchedulingMetrics metrics,
    ILogger<CancelAppointmentHandler> logger)
{
    public async Task<Result<Appointment>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        var appointment = await db.Appointments
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (appointment is null)
        {
            return AppointmentErrors.NotFound;
        }

        var cancellation = appointment.Cancel(time.GetUtcNow());
        if (cancellation.IsFailure)
        {
            return cancellation.Error;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppointmentErrors.ConcurrentChange;
        }

        metrics.Cancelled();
        LogCancelled(logger, appointment.Id);
        return appointment;
    }

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Consulta {AppointmentId} cancelada")]
    private static partial void LogCancelled(ILogger logger, Guid appointmentId);
}

internal static class CancelAppointmentEndpoint
{
    public static async Task<Results<Ok<AppointmentResponse>, ProblemHttpResult>> HandleAsync(
        Guid id,
        CancelAppointmentHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(AppointmentResponse.From(result.Value))
            : result.Error.ToProblem();
    }
}
