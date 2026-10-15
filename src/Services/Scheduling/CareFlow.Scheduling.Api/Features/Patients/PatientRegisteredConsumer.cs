using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Contracts.Patients;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;

namespace CareFlow.Scheduling.Api.Features.Patients;

/// <summary>O cofre cadastrou o paciente: a agenda passa a reconhecer o pseudônimo.</summary>
internal sealed class PatientRegisteredConsumer(SchedulingDbContext db, TimeProvider time)
    : IIntegrationEventConsumer<PatientRegistered>
{
    public async Task HandleAsync(PatientRegistered @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        var now = time.GetUtcNow();
        var patient = await PatientStatusStore.LockAsync(db, context.TenantId, @event.PatientRef, now, cancellationToken).ConfigureAwait(false);
        patient.MarkRegistered(now);
    }
}
