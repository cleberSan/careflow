using CareFlow.Notifications.Domain.Channels;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CareFlow.Notifications.Api.Channels.Resilience;

/// <summary>
/// Saúde dos canais para o Painel: degradado quando algum circuito não está fechado. Nunca "unhealthy": com
/// canais fora, os avisos esperam na fila ou saem pelo fallback, e o serviço continua aceitando eventos.
/// </summary>
internal sealed class ChannelsHealthCheck(ChannelCircuits circuits) : IHealthCheck
{
    public const string Tag = "channels";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = circuits.Snapshot();
        var data = snapshot.ToDictionary(entry => entry.Key.Slug(), entry => (object)entry.Value.ToString());
        var degraded = snapshot.Where(entry => entry.Value != ChannelCircuitState.Closed).Select(entry => entry.Key.Slug()).ToArray();

        return Task.FromResult(degraded.Length == 0
            ? HealthCheckResult.Healthy("Todos os canais com circuito fechado.", data)
            : HealthCheckResult.Degraded($"Canais degradados: {string.Join(", ", degraded)}.", data: data));
    }
}
