using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using CareFlow.BuildingBlocks.Results;
using CareFlow.Scheduling.Api.Common.Http;
using CareFlow.Scheduling.Api.Common.Observability;
using CareFlow.Scheduling.Api.Common.Tenancy;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.Domain.Appointments;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Scheduling.Api.Features.Appointments;

public sealed record BookAppointmentRequest(
    Guid ProfessionalId,
    Guid PatientRef,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

internal sealed record BookingOutcome(Appointment Appointment, bool Replayed);

internal sealed partial class BookAppointmentHandler(
    SchedulingDbContext db,
    ITenantContext tenant,
    SchedulingPolicy policy,
    TimeProvider time,
    SchedulingMetrics metrics,
    ILogger<BookAppointmentHandler> logger)
{
    public async Task<Result<BookingOutcome>> HandleAsync(
        BookAppointmentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(request);

        var existing = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, fingerprint);
        }

        var booking = Appointment.Book(
            new BookingRequest(
                tenant.TenantId,
                request.ProfessionalId,
                request.PatientRef,
                request.StartsAt,
                request.EndsAt,
                idempotencyKey,
                fingerprint),
            policy,
            time.GetUtcNow());

        if (booking.IsFailure)
        {
            return booking.Error;
        }

        var appointment = booking.Value;
        db.Appointments.Add(appointment);

        try
        {
            // O banco é a fonte da verdade: a exclusion constraint resolve a corrida entre reservas
            // simultâneas, sem consulta prévia sujeita a TOCTOU.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.IsExclusionViolation(AppointmentConfiguration.NoOverlapConstraint))
        {
            db.Entry(appointment).State = EntityState.Detached;

            // Retentativas simultâneas com a mesma chave disputam o mesmo horário, então a exclusion
            // constraint pode disparar antes do índice de idempotência. Se a vencedora foi uma requisição
            // com esta chave, é um replay, não um conflito.
            var sameKey = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (sameKey is not null)
            {
                return Replay(sameKey, fingerprint);
            }

            metrics.Conflict();
            LogSlotConflict(logger, request.ProfessionalId);
            return AppointmentErrors.SlotUnavailable;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(AppointmentConfiguration.IdempotencyIndex))
        {
            // Outra requisição com a mesma chave venceu a corrida: responde com o resultado dela.
            db.Entry(appointment).State = EntityState.Detached;
            var winner = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Violação de idempotência sem registro correspondente.", ex);
            return Replay(winner, fingerprint);
        }

        metrics.Booked();
        LogBooked(logger, appointment.Id, appointment.ProfessionalId);
        return new BookingOutcome(appointment, Replayed: false);
    }

    internal static string Fingerprint(BookAppointmentRequest request)
    {
        var canonical = string.Join(
            '|',
            request.ProfessionalId.ToString("N"),
            request.PatientRef.ToString("N"),
            request.StartsAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            request.EndsAt.UtcTicks.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private Task<Appointment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.Appointments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey, cancellationToken);

    private Result<BookingOutcome> Replay(Appointment existing, string fingerprint)
    {
        if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return IdempotencyKey.ReusedWithDifferentPayload;
        }

        metrics.Replayed();
        return new BookingOutcome(existing, Replayed: true);
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Consulta {AppointmentId} reservada para o profissional {ProfessionalId}")]
    private static partial void LogBooked(ILogger logger, Guid appointmentId, Guid professionalId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Reserva recusada: horário ocupado para o profissional {ProfessionalId}")]
    private static partial void LogSlotConflict(ILogger logger, Guid professionalId);
}

internal static class BookAppointmentEndpoint
{
    public static async Task<Results<Created<AppointmentResponse>, ProblemHttpResult>> HandleAsync(
        [FromHeader(Name = IdempotencyKey.HeaderName)] string? idempotencyKey,
        BookAppointmentRequest request,
        BookAppointmentHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var key = IdempotencyKey.Validate(idempotencyKey);
        if (key.IsFailure)
        {
            return key.Error.ToProblem();
        }

        var result = await handler.HandleAsync(request, key.Value, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var (appointment, replayed) = result.Value;
        if (replayed)
        {
            response.Headers[IdempotencyKey.ReplayedHeaderName] = "true";
        }

        return TypedResults.Created($"/v1/appointments/{appointment.Id}", AppointmentResponse.From(appointment));
    }
}
