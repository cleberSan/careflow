using CareFlow.Audit.Api.Infrastructure.Persistence;
using CareFlow.Audit.IntegrationTests.Infrastructure;
using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Chain;

public sealed class MigrationTests(AuditApiFixture api)
{
    [Fact]
    public async Task Migrations_CanBeRolledBackAndReapplied()
    {
        // Arrange: banco descartável, para não afetar a instância usada pelos outros testes.
        var database = $"rollback_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(api.AdminConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(api.AdminConnectionString) { Database = database, Pooling = false }.ConnectionString;
            var options = new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(connectionString).Options;
            await using var db = new AuditDbContext(options, NoTenantContext.Instance);

            // Act
            await db.Database.MigrateAsync(Ct);
            await db.Database.MigrateAsync(Migration.InitialDatabase, Ct);
            await db.Database.MigrateAsync(Ct);

            // Assert
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
            var policies = await db.Database
                .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM pg_policies WHERE tablename IN ('audit_entries', 'audit_chain_heads')""")
                .SingleAsync(Ct);
            Assert.Equal(2, policies);
            var triggers = await db.Database
                .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM pg_trigger WHERE tgname LIKE 'tr_audit_entries_%'""")
                .SingleAsync(Ct);
            Assert.Equal(2, triggers);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(api.AdminConnectionString);
            await admin.OpenAsync(Ct);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(Ct);
        }
    }
}
