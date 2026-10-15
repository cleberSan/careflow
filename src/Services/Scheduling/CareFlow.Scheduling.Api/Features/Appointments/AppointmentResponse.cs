using CareFlow.Scheduling.Domain.Appointments;

namespace CareFlow.Scheduling.Api.Features.Appointments;

public sealed record AppointmentResponse(
    Guid Id,
    Guid ProfessionalId,
    Guid PatientRef,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    AppointmentStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancelledAt,
    CancellationReason? CancellationReason)
{
    public static AppointmentResponse From(Appointment appointment)
    {
        ArgumentNullException.ThrowIfNull(appointment);

        return new AppointmentResponse(
            appointment.Id,
            appointment.ProfessionalId,
            appointment.PatientRef,
            appointment.StartsAt,
            appointment.EndsAt,
            appointment.Status,
            appointment.CreatedAt,
            appointment.CancelledAt,
            appointment.CancellationReason);
    }
}
