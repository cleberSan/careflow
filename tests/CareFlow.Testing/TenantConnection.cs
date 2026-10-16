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

    public static async Task<T?> ScalarAsync<T>(this NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}

/// <summary>Comandos como admin (sem RLS), para preparar cenários e conferir o banco por fora da API.</summary>
public static class AdminSql
{
    public static async Task<int> ExecuteAsync(string adminConnectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await TenantConnection.OpenAsync(adminConnectionString, tenantId: null);
        return await connection.ExecuteAsync(sql, parameters);
    }

    public static async Task<T?> ScalarAsync<T>(string adminConnectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await TenantConnection.OpenAsync(adminConnectionString, tenantId: null);
        return await connection.ScalarAsync<T>(sql, parameters);
    }
}
