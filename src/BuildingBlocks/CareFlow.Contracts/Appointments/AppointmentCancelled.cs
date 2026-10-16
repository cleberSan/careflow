namespace CareFlow.Contracts.Appointments;

/// <summary>
/// A agenda cancelou uma consulta. Traz o horário para que o aviso possa ser composto mesmo que este evento
/// chegue antes do <see cref="AppointmentBooked"/>. <see cref="Reason"/> é um dos valores de
/// <see cref="AppointmentCancellationReasons"/>.
/// </summary>
public sealed record AppointmentCancelled(
    Guid AppointmentId,
    Guid PatientRef,
    DateTimeOffset StartsAt,
    string Reason,
    DateTimeOffset CancelledAt) : IIntegrationEvent
{
    public static string EventType => "appointment.cancelled";
}

/// <summary>Motivos de cancelamento como aparecem nos eventos. Texto estável, independente do enum da agenda.</summary>
public static class AppointmentCancellationReasons
{
    /// <summary>Cancelada a pedido, pela API da agenda.</summary>
    public const string Requested = "requested";

    /// <summary>Cancelada porque o paciente revogou o consentimento para agendamento.</summary>
    public const string ConsentRevoked = "consent-revoked";

    /// <summary>Cancelada porque o titular pediu a eliminação. O paciente não recebe aviso de cancelamento.</summary>
    public const string SubjectErasure = "subject-erasure";
}
