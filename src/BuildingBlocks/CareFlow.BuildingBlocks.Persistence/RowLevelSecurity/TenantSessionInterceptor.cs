using System.Data.Common;

using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

/// <summary>
/// Grava a clínica da requisição na sessão do PostgreSQL toda vez que o EF abre uma conexão.
/// As políticas de Row-Level Security leem esse valor; sem clínica, o valor fica vazio e as
/// políticas não devolvem nenhuma linha (falha fechada).
/// </summary>
/// <remarks>
/// O valor é de sessão, não de transação, porque o EF executa consultas fora de transação explícita.
/// Ele é sempre sobrescrito na abertura (inclusive com vazio), então uma conexão reaproveitada do pool
/// nunca carrega a clínica de outra requisição, mesmo que o reset do pool falhe.
/// </remarks>
public sealed class TenantSessionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public const string SettingName = "app.tenant_id";

    private const string Sql = "SELECT set_config('" + SettingName + "', @tenant_id, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var command = CreateCommand(connection);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.Value = tenant.TryGetTenantId(out var tenantId) ? tenantId.ToString() : string.Empty;
        command.Parameters.Add(parameter);

        return command;
    }
}
