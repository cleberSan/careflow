using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Contracts.Consents;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;

namespace CareFlow.Scheduling.Api.Features.Patients;

/// <summary>Base legal concedida. A agenda só olha a finalidade "agendamento" e ignora versões já conhecidas.</summary>
internal sealed class ConsentGrantedConsumer(SchedulingDbContext db, TimeProvider time)
    : IIntegrationEventConsumer<ConsentGranted>
{
    public async Task HandleAsync(ConsentGranted @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(@event.Purpose, ConsentPurposes.Scheduling, StringComparison.Ordinal))
        {
            return;
        }

        PatientStatusStore.EnsureValidVersion(@event.Version);
        var now = time.GetUtcNow();
        var patient = await PatientStatusStore.LockAsync(db, context.TenantId, @event.PatientRef, now, cancellationToken).ConfigureAwait(false);
        patient.ApplyConsentGranted(@event.Version, now);
    }
}
