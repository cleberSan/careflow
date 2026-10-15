using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Npgsql;

namespace CareFlow.BuildingBlocks.Persistence.Demo;

/// <summary>
/// Apaga todas as linhas de uma clínica no banco do serviço: toda tabela do schema <c>public</c> com coluna
/// <c>tenant_id</c> (inclusive Outbox e Inbox), em ordem segura para as chaves estrangeiras. Tabelas novas entram
/// sozinhas; só ficam de fora as listadas em <c>keep</c>. Existe para o ambiente de demonstração (ADR-0010).
/// </summary>
/// <remarks>
/// Roda com o papel dono do schema, mas a clínica vai para <c>app.tenant_id</c> na transação: com FORCE RLS, o
/// próprio banco recusa tocar linhas de outra clínica mesmo se o filtro do comando estivesse errado. Os nomes das
/// tabelas vêm do catálogo e saem entre aspas, nunca de entrada externa.
/// </remarks>
public static class TenantDataPurge
{
    private const string TenantTablesSql = """
        SELECT c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.table_schema = 'public' AND c.column_name = 'tenant_id' AND t.table_type = 'BASE TABLE'
        """;

    private const string ForeignKeysSql = """
        SELECT child.relname, parent.relname
        FROM pg_constraint k
        JOIN pg_class child ON child.oid = k.conrelid
        JOIN pg_class parent ON parent.oid = k.confrelid
        WHERE k.contype = 'f' AND k.connamespace = 'public'::regnamespace AND k.conrelid <> k.confrelid
        """;

    public static async Task<int> PurgeAsync(
        string ownerConnectionString,
        Guid tenantId,
        IReadOnlyCollection<string> keep,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        ArgumentNullException.ThrowIfNull(keep);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Clínica vazia.", nameof(tenantId));
        }

        var connection = new NpgsqlConnection(ownerConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var tables = (await ReadPairsAsync(connection, transaction, TenantTablesSql, cancellationToken).ConfigureAwait(false))
                    .Select(row => row.First)
                    .Where(table => !keep.Contains(table, StringComparer.Ordinal))
                    .ToHashSet(StringComparer.Ordinal);
                var references = await ReadPairsAsync(connection, transaction, ForeignKeysSql, cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(
                    connection,
                    transaction,
                    $"SELECT set_config('{TenantSessionInterceptor.SettingName}', @tenant, true)",
                    tenantId,
                    cancellationToken).ConfigureAwait(false);

                var rows = 0;
                foreach (var table in DeletionOrder(tables, references))
                {
                    rows += await ExecuteAsync(
                        connection,
                        transaction,
                        $"DELETE FROM {Quote(table)} WHERE tenant_id = @tenant::uuid",
                        tenantId,
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return rows;
            }
        }
    }

    /// <summary>
    /// Quem referencia vem antes de quem é referenciado. Ciclos (que o CareFlow não tem) caem para o fim, em ordem
    /// alfabética: o DELETE falha e a transação inteira volta, sem apagar pela metade.
    /// </summary>
    public static IReadOnlyList<string> DeletionOrder(IReadOnlySet<string> tables, IEnumerable<(string Child, string Parent)> references)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(references);

        var children = tables.ToDictionary(table => table, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (child, parent) in references)
        {
            if (tables.Contains(child) && tables.Contains(parent))
            {
                children[parent].Add(child);
            }
        }

        var ordered = new List<string>(tables.Count);
        var pending = new SortedSet<string>(tables, StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(table => children[table].All(child => !pending.Contains(child)));
            ready ??= pending.Min!;
            ordered.Add(ready);
            pending.Remove(ready);
        }

        return ordered;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<List<(string First, string Second)>> ReadPairsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var rows = new List<(string, string)>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((reader.GetString(0), reader.FieldCount > 1 ? reader.GetString(1) : string.Empty));
                }

                return rows;
            }
        }
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("tenant", tenantId.ToString());
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
