using CareFlow.PatientVault.Api.Infrastructure.Persistence;
using CareFlow.PatientVault.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

using Npgsql;

namespace CareFlow.PatientVault.IntegrationTests.Deployment;

/// <summary>
/// Etapa de migrations do deploy (ADR-0013): um container de uma execução só aplica as migrations com o papel
/// dono e termina; a API sobe depois, sem a senha desse papel.
/// </summary>
public sealed class MigrationStepTests(VaultApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MigrateAndExit_AppliesTheMigrations_AndAsksTheProcessToStop()
    {
        // Arrange
        var connectionString = await api.Postgres.CreateDatabaseAsync($"vault_migrate_{Guid.NewGuid():N}");
        await using var app = Build(connectionString, ("Database:MigrateAndExit", "true"));

        // Act
        var exit = await app.MigrateIfEnabledAsync();

        // Assert
        Assert.True(exit);
        Assert.True(await TableExistsAsync(connectionString, "patients"));
    }

    [Fact]
    public async Task WithoutAnyFlag_NothingIsMigrated_AndTheApiKeepsStarting()
    {
        // Arrange
        var connectionString = await api.Postgres.CreateDatabaseAsync($"vault_nomigrate_{Guid.NewGuid():N}");
        await using var app = Build(connectionString);

        // Act
        var exit = await app.MigrateIfEnabledAsync();

        // Assert
        Assert.False(exit);
        Assert.False(await TableExistsAsync(connectionString, "patients"));
    }

    [Fact]
    public async Task MigrateAndExit_WithoutTheMigrationsConnectionString_FailsLoudly()
    {
        // Arrange
        await using var app = Build(connectionString: null, ("Database:MigrateAndExit", "true"));

        // Act
        var act = () => app.MigrateIfEnabledAsync();

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    private static WebApplication Build(string? connectionString, params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder();
        var values = settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value);
        if (connectionString is not null)
        {
            values["ConnectionStrings:PatientVaultMigrations"] = connectionString;
        }

        builder.Configuration.AddInMemoryCollection(values);
        return builder.Build();
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection);
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }
}
