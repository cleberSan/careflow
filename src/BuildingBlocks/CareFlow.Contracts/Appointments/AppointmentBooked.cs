namespace CareFlow.Contracts.Appointments;

/// <summary>
/// A agenda reservou uma consulta. Leva só o necessário para avisar e lembrar o paciente: pseudônimo e
/// horário, sem profissional nem especialidade (que já diriam algo sobre a saúde do paciente).
/// </summary>
public sealed record AppointmentBooked(
    Guid AppointmentId,
    Guid PatientRef,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset BookedAt) : IIntegrationEvent
{
    public static string EventType => "appointment.booked";
}
