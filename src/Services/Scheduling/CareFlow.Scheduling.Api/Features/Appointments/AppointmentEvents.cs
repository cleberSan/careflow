using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Contracts.Appointments;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.Domain.Appointments;

namespace CareFlow.Scheduling.Api.Features.Appointments;

/// <summary>
/// Eventos de consulta para os outros serviços (o Notifications avisa o paciente). Vão pelo Outbox, na mesma
/// transação da reserva ou do cancelamento: não existe consulta sem evento nem evento sem consulta.
/// </summary>
internal static class AppointmentEvents
{
    public static void PublishBooked(this SchedulingDbContext db, Appointment appointment, DateTimeOffset now) =>
        db.Enqueue(
            appointment.TenantId,
            new AppointmentBooked(appointment.Id, appointment.PatientRef, appointment.StartsAt, appointment.EndsAt, now),
            now);

    public static void PublishCancelled(this SchedulingDbContext db, Appointment appointment, DateTimeOffset now) =>
        db.Enqueue(
            appointment.TenantId,
            new AppointmentCancelled(appointment.Id, appointment.PatientRef, appointment.StartsAt, ReasonOf(appointment), now),
            now);

    internal static string ReasonOf(Appointment appointment) => appointment.CancellationReason switch
    {
        CancellationReason.ConsentRevoked => AppointmentCancellationReasons.ConsentRevoked,
        CancellationReason.Requested => AppointmentCancellationReasons.Requested,
        CancellationReason.SubjectErasure => AppointmentCancellationReasons.SubjectErasure,
        _ => throw new InvalidOperationException("Consulta cancelada sem motivo registrado."),
    };
}
