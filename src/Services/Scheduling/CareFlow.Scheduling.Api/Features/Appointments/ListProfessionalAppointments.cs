using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Scheduling.Api.Common.Http;
using CareFlow.Scheduling.Api.Infrastructure;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.Domain.Appointments;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CareFlow.Scheduling.Api.Features.Appointments;

/// <summary>Horário ocupado na agenda. Não expõe o paciente: quem lista a agenda só precisa saber o que está livre.</summary>
public sealed record BusySlotResponse(Guid AppointmentId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

internal static class ListProfessionalAppointmentsEndpoint
{
    public static readonly Error InvalidRange =
        Error.Validation("Agenda.PeriodoInvalido", "Informe 'from' e 'to', com 'to' posterior a 'from'.");

    public static Error RangeTooLong(int maximumDays) =>
        Error.Validation("Agenda.PeriodoMuitoLongo", $"O período consultado pode ter no máximo {maximumDays} dias.");

    public static async Task<Results<Ok<BusySlotResponse[]>, ProblemHttpResult>> HandleAsync(
        Guid professionalId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        SchedulingDbContext db,
        IOptions<SchedulingOptions> options,
        CancellationToken cancellationToken)
    {
        if (from is null || to is null || to <= from)
        {
            return InvalidRange.ToProblem();
        }

        var maximumDays = options.Value.MaximumListRangeDays;
        if (to.Value - from.Value > TimeSpan.FromDays(maximumDays))
        {
            return RangeTooLong(maximumDays).ToProblem();
        }

        var start = from.Value.ToUniversalTime();
        var end = to.Value.ToUniversalTime();

        var slots = await db.Appointments
            .AsNoTracking()
            .Where(a => a.ProfessionalId == professionalId
                && a.Status == AppointmentStatus.Booked
                && a.StartsAt < end
                && a.EndsAt > start)
            .OrderBy(a => a.StartsAt)
            .Select(a => new BusySlotResponse(a.Id, a.StartsAt, a.EndsAt))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(slots);
    }
}
