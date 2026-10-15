namespace CareFlow.Notifications.Domain.Appointments;

/// <summary>
/// O que o Notifications sabe de uma consulta, pelos eventos da agenda. Reserva e cancelamento podem chegar
/// em qualquer ordem; como uma consulta cancelada nunca volta a ser reservada, o cancelamento é terminal e
/// uma reserva que chega depois dele é ignorada, sem precisar de versão.
/// </summary>
public sealed class AppointmentTrack
{
    private AppointmentTrack()
    {
        // Usado pelo EF Core.
    }

    public Guid TenantId { get; private set; }

    public Guid AppointmentId { get; private set; }

    public Guid PatientRef { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public AppointmentState State { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AppointmentTrack Track(Guid tenantId, Guid appointmentId, Guid patientRef, DateTimeOffset startsAt, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || appointmentId == Guid.Empty || patientRef == Guid.Empty)
        {
            throw new ArgumentException("Clínica, consulta e paciente são obrigatórios.");
        }

        return new AppointmentTrack
        {
            TenantId = tenantId,
            AppointmentId = appointmentId,
            PatientRef = patientRef,
            StartsAt = startsAt.ToUniversalTime(),
            State = AppointmentState.Unknown,
            UpdatedAt = now,
        };
    }

    /// <summary>Aplica a reserva. <c>true</c> se ela é nova e o paciente deve ser avisado.</summary>
    public bool ApplyBooked(DateTimeOffset now)
    {
        if (State != AppointmentState.Unknown)
        {
            // Reserva repetida, ou cancelamento que chegou antes: nada a avisar.
            return false;
        }

        State = AppointmentState.Booked;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Aplica o cancelamento. <c>true</c> se ele é novo.</summary>
    public bool ApplyCancelled(DateTimeOffset now)
    {
        if (State == AppointmentState.Cancelled)
        {
            return false;
        }

        State = AppointmentState.Cancelled;
        UpdatedAt = now;
        return true;
    }
}
