using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Scheduling.Domain.Appointments;

/// <summary>
/// Consulta reservada com um profissional. O paciente é referenciado apenas por pseudônimo
/// (<see cref="PatientRef"/>): dados pessoais ficam no Patient Vault, nunca neste serviço.
/// </summary>
public sealed class Appointment
{
    private Appointment()
    {
        // Usado pelo EF Core.
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProfessionalId { get; private set; }

    public Guid PatientRef { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public AppointmentStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public CancellationReason? CancellationReason { get; private set; }

    /// <summary>Chave de idempotência enviada pelo cliente na criação.</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>Hash do conteúdo da requisição original, para detectar reuso da chave com outro conteúdo.</summary>
    public string RequestFingerprint { get; private set; } = string.Empty;

    public static Result<Appointment> Book(
        BookingRequest request,
        SchedulingPolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Fingerprint);

        if (request.TenantId == Guid.Empty || request.ProfessionalId == Guid.Empty || request.PatientRef == Guid.Empty)
        {
            return AppointmentErrors.MissingIdentifier;
        }

        var startsAt = request.StartsAt.ToUniversalTime();
        var endsAt = request.EndsAt.ToUniversalTime();

        if (endsAt <= startsAt)
        {
            return AppointmentErrors.InvalidInterval;
        }

        if (!IsAlignedToMinute(startsAt) || !IsAlignedToMinute(endsAt))
        {
            return AppointmentErrors.NotAlignedToMinute;
        }

        var duration = endsAt - startsAt;
        if (duration < policy.MinimumDuration || duration > policy.MaximumDuration)
        {
            return AppointmentErrors.DurationOutOfRange(policy);
        }

        if (startsAt <= now)
        {
            return AppointmentErrors.StartsInThePast;
        }

        return new Appointment
        {
            Id = Guid.CreateVersion7(now),
            TenantId = request.TenantId,
            ProfessionalId = request.ProfessionalId,
            PatientRef = request.PatientRef,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Status = AppointmentStatus.Booked,
            CreatedAt = ToStoragePrecision(now),
            IdempotencyKey = request.IdempotencyKey,
            RequestFingerprint = request.Fingerprint,
        };
    }

    public Result Cancel(DateTimeOffset now) => Cancel(now, Appointments.CancellationReason.Requested);

    /// <summary>
    /// Cancela porque o paciente revogou o consentimento para agendamento. Só vale para consultas futuras e
    /// ainda ativas; as que já começaram ficam como estão (o atendimento aconteceu sob a base vigente).
    /// </summary>
    public Result CancelForConsentRevocation(DateTimeOffset now) => Cancel(now, Appointments.CancellationReason.ConsentRevoked);

    private Result Cancel(DateTimeOffset now, CancellationReason reason)
    {
        if (Status == AppointmentStatus.Cancelled)
        {
            return Result.Failure(AppointmentErrors.AlreadyCancelled);
        }

        if (StartsAt <= now)
        {
            return Result.Failure(AppointmentErrors.AlreadyStarted);
        }

        Status = AppointmentStatus.Cancelled;
        CancelledAt = ToStoragePrecision(now);
        CancellationReason = reason;
        return Result.Success();
    }

    private static bool IsAlignedToMinute(DateTimeOffset value) => value.Ticks % TimeSpan.TicksPerMinute == 0;

    /// <summary>
    /// Trunca para microssegundos, a precisão do <c>timestamptz</c> do PostgreSQL, para que a resposta da
    /// criação seja idêntica à leitura posterior e ao replay idempotente.
    /// </summary>
    private static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
