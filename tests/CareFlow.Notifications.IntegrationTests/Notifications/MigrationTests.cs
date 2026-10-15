using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

public sealed class MigrationTests(NotificationsApiFixture api)
{
    [Fact]
    public async Task Migrations_CanBeRolledBackAndReapplied_WithRlsOnEveryTable()
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
            var options = new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(connectionString).Options;
            await using var db = new NotificationsDbContext(options, NoTenantContext.Instance);

            // Act
            await db.Database.MigrateAsync(Ct);
            await db.Database.MigrateAsync(Migration.InitialDatabase, Ct);
            await db.Database.MigrateAsync(Ct);

            // Assert
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
            var withoutRls = await db.Database
                .SqlQueryRaw<string>("""
                    SELECT c.relname AS "Value" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory'
                      AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
                    """)
                .ToListAsync(Ct);
            Assert.Empty(withoutRls);

            // 8 tabelas com tenant_isolation; Outbox, Inbox e fila com relay_access.
            var policies = await db.Database
                .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM pg_policies WHERE schemaname = 'public'""")
                .SingleAsync(Ct);
            Assert.Equal(11, policies);
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
