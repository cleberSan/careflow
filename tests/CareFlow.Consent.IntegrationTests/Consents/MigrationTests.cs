using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Consent.Api.Infrastructure.Persistence;
using CareFlow.Consent.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

using static CareFlow.Consent.IntegrationTests.Infrastructure.ConsentClient;

namespace CareFlow.Consent.IntegrationTests.Consents;

public sealed class MigrationTests(ConsentApiFixture api)
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
            var options = new DbContextOptionsBuilder<ConsentDbContext>().UseNpgsql(connectionString).Options;
            await using var db = new ConsentDbContext(options, NoTenantContext.Instance);

            // Act
            await db.Database.MigrateAsync(Ct);
            await db.Database.MigrateAsync(Migration.InitialDatabase, Ct);
            await db.Database.MigrateAsync(Ct);

            // Assert
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
            var policies = await db.Database
                .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM pg_policies WHERE tablename IN ('consents', 'outbox_messages')""")
                .SingleAsync(Ct);
            // consents: tenant_isolation; outbox: tenant_isolation + relay_access.
            Assert.Equal(3, policies);
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
