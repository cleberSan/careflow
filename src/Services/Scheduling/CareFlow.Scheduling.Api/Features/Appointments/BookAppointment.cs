using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Results;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Scheduling.Api.Common;
using CareFlow.Scheduling.Api.Common.Http;
using CareFlow.Scheduling.Api.Common.Observability;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.Domain.Appointments;
using CareFlow.Scheduling.Domain.Patients;

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
        string actor,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(request);

        var existing = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, fingerprint);
        }

        var now = time.GetUtcNow();
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
            now);

        if (booking.IsFailure)
        {
            return booking.Error;
        }

        var appointment = booking.Value;
        var saved = await SaveAsync(appointment, actor, now, cancellationToken).ConfigureAwait(false);
        switch (saved)
        {
            case SaveOutcome.Saved:
                metrics.Booked();
                LogBooked(logger, appointment.Id, appointment.ProfessionalId);
                return new BookingOutcome(appointment, Replayed: false);

            case SaveOutcome.PatientUnknown:
                metrics.Ineligible(PatientErrors.NotYetKnown.Code);
                return PatientErrors.NotYetKnown;

            case SaveOutcome.NoLegalBasis:
                metrics.Ineligible(PatientErrors.NoLegalBasis.Code);
                return PatientErrors.NoLegalBasis;

            case SaveOutcome.SlotTaken:
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

            default:
                // Outra requisição com a mesma chave venceu a corrida: responde com o resultado dela.
                var winner = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Violação de idempotência sem registro correspondente.");
                return Replay(winner, fingerprint);
        }
    }

    /// <summary>
    /// Numa transação: trava a projeção do paciente em modo compartilhado, confere a elegibilidade e grava a
    /// consulta com o registro de auditoria. A trava serializa com a revogação: se ela for processada ao mesmo
    /// tempo, ou espera esta reserva terminar (e então a cancela), ou esta reserva espera e vê o paciente revogado.
    /// </summary>
    private async Task<SaveOutcome> SaveAsync(Appointment appointment, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var patient = await db.PatientStatuses
                .FromSql($"SELECT * FROM patient_statuses WHERE tenant_id = {appointment.TenantId} AND patient_ref = {appointment.PatientRef} FOR SHARE")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (patient is null)
            {
                return SaveOutcome.PatientUnknown;
            }

            var eligibility = patient.EnsureCanBook();
            if (eligibility.IsFailure)
            {
                return eligibility.Error == PatientErrors.NotYetKnown ? SaveOutcome.PatientUnknown : SaveOutcome.NoLegalBasis;
            }

            db.Appointments.Add(appointment);
            db.RecordAccess(
                appointment.TenantId, SchedulingAudit.SourceService, actor, "appointment.booked", SchedulingAudit.ResourceType, appointment.PatientRef, now);
            db.PublishBooked(appointment, now);

            try
            {
                // O banco é a fonte da verdade: a exclusion constraint resolve a corrida entre reservas
                // simultâneas, sem consulta prévia sujeita a TOCTOU.
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SaveOutcome.Saved;
            }
            catch (DbUpdateException ex) when (ex.IsExclusionViolation(AppointmentConfiguration.NoOverlapConstraint))
            {
                db.ChangeTracker.Clear();
                return SaveOutcome.SlotTaken;
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation(AppointmentConfiguration.IdempotencyIndex))
            {
                db.ChangeTracker.Clear();
                return SaveOutcome.KeyTaken;
            }
        }
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

    private enum SaveOutcome
    {
        Saved,
        PatientUnknown,
        NoLegalBasis,
        SlotTaken,
        KeyTaken,
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

        var result = await handler.HandleAsync(request, key.Value, AuditActor.From(response.HttpContext.User), cancellationToken).ConfigureAwait(false);
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
