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
/// Monta o plano de envio da clínica do escopo: a falha gravada para a clínica (só clínicas isoladas, com a API de
/// falhas liberada e dentro do prazo) vale no lugar da configuração geral. Lida a cada aviso, do banco: todas as
/// instâncias do serviço enxergam a mesma falha, o que resolve o "falha por processo" do ADR-0009 para o demo.
/// </summary>
internal sealed class ClinicFaultResolver(
    NotificationsDbContext db,
    ClinicIsolation isolation,
    IOptionsMonitor<FaultInjectionOptions> faults,
    TimeProvider time)
{
    public async Task<ClinicChannelPlan> PlanAsync(Guid clinic, CancellationToken cancellationToken)
    {
        var general = faults.CurrentValue;
        var effective = Enum.GetValues<Channel>().ToDictionary(channel => channel, general.For);

        if (isolation.OverridesEnabled && isolation.IsIsolated(clinic))
        {
            var now = time.GetUtcNow();
            var overrides = await db.ChannelFaults.AsNoTracking()
                .Where(f => f.ExpiresAt > now)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var fault in overrides)
            {
                effective[fault.Channel] = fault.ToOptions();
            }
        }

        return new ClinicChannelPlan(isolation.PartitionFor(clinic), effective);
    }
}
