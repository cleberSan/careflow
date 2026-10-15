using CareFlow.BuildingBlocks.Messaging.Broker;
using CareFlow.BuildingBlocks.Messaging.Outbox;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace CareFlow.BuildingBlocks.Messaging.Diagnostics;

/// <summary>
/// Broker fora não tira o serviço de prontidão: é justamente para isso que existe o Outbox. Fica só no
/// grupo <c>messaging</c>, para alertas e para o painel.
/// </summary>
public sealed class BrokerHealthCheck(RabbitMqConnection broker) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(broker.IsOpen
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Sem conexão com o RabbitMQ; eventos aguardam no Outbox."));
}

/// <summary>Readiness: o login do despachante também não pode ignorar RLS (superusuário ou BYPASSRLS).</summary>
public sealed class RelayRoleHealthCheck(RelayDataSource relay) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connection = await relay.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using var command = new NpgsqlCommand(
                "SELECT (rolsuper OR rolbypassrls) FROM pg_roles WHERE rolname = current_user", connection);
            var bypasses = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            return bypasses
                ? HealthCheckResult.Unhealthy("O login do despachante usa um papel que ignora Row-Level Security.")
                : HealthCheckResult.Healthy();
        }
    }
}
