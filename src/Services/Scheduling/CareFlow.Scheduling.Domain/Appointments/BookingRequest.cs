namespace CareFlow.Scheduling.Domain.Appointments;

/// <summary>
/// Dados de uma nova reserva. <see cref="IdempotencyKey"/> e <see cref="Fingerprint"/> vêm da borda HTTP
/// e são guardados com a consulta para tornar a criação idempotente.
/// </summary>
public sealed record BookingRequest(
    Guid TenantId,
    Guid ProfessionalId,
    Guid PatientRef,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string IdempotencyKey,
    string Fingerprint);
