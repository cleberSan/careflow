using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

/// <summary>
/// Readiness: recusa tráfego se a API estiver conectada com um papel que ignora RLS
/// (superusuário ou BYPASSRLS). É a proteção contra uma connection string de admin em produção.
/// </summary>
public sealed class RowLevelSecurityHealthCheck<TContext>(TContext db) : IHealthCheck
    where TContext : DbContext
{
    private const string Sql =
        """SELECT (rolsuper OR rolbypassrls) AS "Value" FROM pg_roles WHERE rolname = current_user""";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var bypasses = await db.Database
            .SqlQueryRaw<bool>(Sql)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        return bypasses
            ? HealthCheckResult.Unhealthy("A conexão da aplicação usa um papel que ignora Row-Level Security.")
            : HealthCheckResult.Healthy();
    }
}
