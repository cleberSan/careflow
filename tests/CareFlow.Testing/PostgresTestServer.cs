using System.Security.Cryptography;

using Npgsql;

using Testcontainers.PostgreSql;

namespace CareFlow.Testing;

/// <summary>
/// PostgreSQL real para testes de integração (Testcontainers), ou um servidor existente quando
/// <c>CAREFLOW_TEST_POSTGRES</c> aponta para ele. A connection string de admin roda as migrations
/// (como no pipeline de deploy); a API usa um login próprio, sem superusuário, para que o RLS valha.
/// </summary>
public sealed class PostgresTestServer : IAsyncDisposable
{
    private readonly PostgreSqlContainer? _container;

    private PostgresTestServer(PostgreSqlContainer? container, string adminConnectionString)
    {
        _container = container;
        AdminConnectionString = adminConnectionString;
    }

    public string AdminConnectionString { get; }

    public static async Task<PostgresTestServer> StartAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("CAREFLOW_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return new PostgresTestServer(null, connectionString);
        }

        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        return new PostgresTestServer(container, container.GetConnectionString());
    }

    /// <summary>
    /// Gera nome, senha aleatória e connection string de um login da API. O login em si só é criado por
    /// <see cref="CreateLoginAsync"/>, depois que as migrations criaram o papel da aplicação.
    /// </summary>
    public ApplicationLogin NewApplicationLogin(string name, string? database = null)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Username = name,
            Password = password,
        };
        if (database is not null)
        {
            builder.Database = database;
        }

        return new ApplicationLogin(name, password, builder.ConnectionString);
    }

    /// <summary>
    /// Cria um banco (um por serviço, como em produção) e devolve a connection string de admin dele. Logins e
    /// papéis são do servidor, então valem em todos os bancos.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        EnsureIdentifier(database);
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database }.ConnectionString;
    }

    /// <summary>Cria um papel sem login, se ainda não existir (as migrations também o criam, de forma idempotente).</summary>
    public async Task EnsureRoleAsync(string role)
    {
        EnsureIdentifier(role);
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{role}') THEN
                    CREATE ROLE "{role}" NOLOGIN NOSUPERUSER NOBYPASSRLS;
                END IF;
            END
            $$;
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Cria (ou atualiza a senha de) um login sem privilégios, membro do papel da aplicação.</summary>
    public async Task CreateLoginAsync(ApplicationLogin login, string memberOf)
    {
        ArgumentNullException.ThrowIfNull(login);

        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        // Nomes são constantes dos testes e a senha é hexadecimal, então não há o que escapar.
        await using var command = new NpgsqlCommand(
            $"""
            DO $$
            BEGIN
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = '{login.Name}') THEN
                    ALTER ROLE "{login.Name}" WITH LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{login.Password}';
                ELSE
                    CREATE ROLE "{login.Name}" WITH LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{login.Password}';
                END IF;
            END
            $$;
            GRANT "{memberOf}" TO "{login.Name}";
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    // Nomes são constantes dos testes; o formato restrito impede SQL arbitrário.
    private static void EnsureIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new ArgumentException($"Identificador inválido: '{name}'.", nameof(name));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

public sealed record ApplicationLogin(string Name, string Password, string ConnectionString);
