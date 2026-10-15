using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Channels;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Channels.Resilience;

/// <summary>Partição e falha efetiva de cada canal para os envios de uma clínica.</summary>
internal sealed record ClinicChannelPlan(string Partition, IReadOnlyDictionary<Channel, ChannelFaultOptions?> Faults)
{
    public ChannelCall CallFor(Channel channel) => new(Partition, Faults.GetValueOrDefault(channel));
}

/// <summary>
/// Monta o plano de envio da clínica do escopo (a do tenant, com RLS): se ela está numa partição isolada dentro do
/// prazo, os envios contam no circuito dessa partição e a falha gravada para a clínica vale no lugar da
/// configuração geral. Lido a cada aviso, do banco: todas as instâncias do serviço enxergam a mesma falha, o que
/// resolve o "falha por processo" do ADR-0009 para o demo.
/// </summary>
internal sealed class ClinicFaultResolver(
    NotificationsDbContext db,
    ClinicIsolation isolation,
    IOptionsMonitor<FaultInjectionOptions> faults,
    TimeProvider time)
{
    /// <summary>A partição isolada vigente da clínica do escopo, ou nula se ela usa o circuito compartilhado.</summary>
    public async Task<string?> ActivePartitionAsync(CancellationToken cancellationToken)
    {
        if (!isolation.OverridesEnabled)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var slot = await db.ClinicPartitions.AsNoTracking()
            .Where(p => p.ExpiresAt > now)
            .Select(p => (int?)p.Slot)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // Uma partição acima do configurado (configuração reduzida com sessões em curso) volta ao compartilhado.
        return slot is { } value && isolation.IsValidSlot(value) ? ClinicIsolation.Label(value) : null;
    }

    public async Task<ClinicChannelPlan> PlanAsync(CancellationToken cancellationToken)
    {
        var general = faults.CurrentValue;
        var effective = Enum.GetValues<Channel>().ToDictionary(channel => channel, general.For);

        var partition = await ActivePartitionAsync(cancellationToken).ConfigureAwait(false);
        if (partition is null)
        {
            return new ClinicChannelPlan(ClinicIsolation.Shared, effective);
        }

        var now = time.GetUtcNow();
        var overrides = await db.ChannelFaults.AsNoTracking()
            .Where(f => f.ExpiresAt > now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var fault in overrides)
        {
            effective[fault.Channel] = fault.ToOptions();
        }

        return new ClinicChannelPlan(partition, effective);
    }
}
