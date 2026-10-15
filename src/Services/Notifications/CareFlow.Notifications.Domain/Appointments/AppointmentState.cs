namespace CareFlow.Notifications.Domain.Appointments;

public enum AppointmentState
{
    /// <summary>Linha criada por um evento que ainda não foi aplicado (só existe dentro da transação).</summary>
    Unknown = 0,
    Booked,
    Cancelled,
}
