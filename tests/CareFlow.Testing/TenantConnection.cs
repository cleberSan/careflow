using Npgsql;

namespace CareFlow.Testing;

/// <summary>Acesso direto ao banco, fora da API, para provar o que o RLS e a cifragem garantem por si só.</summary>
public static class TenantConnection
{
    /// <summary>Abre uma conexão e grava a clínica na sessão, como a API faz. Sem clínica, grava vazio.</summary>
    public static async Task<NpgsqlConnection> OpenAsync(string connectionString, Guid? tenantId)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant_id, false)", connection);
        command.Parameters.AddWithValue("tenant_id", tenantId?.ToString() ?? string.Empty);
        await command.ExecuteNonQueryAsync();

        return connection;
    }

    public static async Task<long> CountAsync(this NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<int> ExecuteAsync(this NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }
}
