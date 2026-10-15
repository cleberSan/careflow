using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace CareFlow.Scheduling.IntegrationTests.Appointments;

public sealed class MigrationTests(SchedulingApiFixture api)
{
    [Fact]
    public async Task Migrations_CanBeRolledBackAndReapplied()
    {
        // Arrange: banco descartável, para não afetar a instância usada pelos outros testes.
        var database = $"rollback_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(api.AdminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(api.AdminConnectionString) { Database = database, Pooling = false }.ConnectionString;
            var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseNpgsql(connectionString).Options;
            await using var db = new SchedulingDbContext(options, NoTenantContext.Instance);

            // Act
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await db.Database.MigrateAsync(Migration.InitialDatabase, TestContext.Current.CancellationToken);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            // Assert
            var pending = await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
            Assert.Empty(pending);
            var policies = await db.Database
                .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM pg_policies WHERE tablename = 'appointments'""")
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, policies);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(api.AdminConnectionString);
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void SynchronousQueries_AlsoCarryTheClinicToTheSession()
    {
        // Arrange: mesmo caminho da API (interceptor no DbContext), mas com chamada síncrona do EF.
        var tenant = new FixedTenant(Guid.NewGuid());
        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql(api.ApplicationConnectionString)
            .AddInterceptors(new TenantSessionInterceptor(tenant))
            .Options;
        using var db = new SchedulingDbContext(options, tenant);

        // Act
        var setting = db.Database
            .SqlQueryRaw<string>("""SELECT current_setting('app.tenant_id') AS "Value" """)
            .AsEnumerable()
            .Single();

        // Assert
        Assert.Equal(tenant.TenantId.ToString(), setting);
    }

    private sealed class FixedTenant(Guid tenantId) : ITenantContext
    {
        public Guid TenantId => tenantId;

        public bool TryGetTenantId(out Guid id)
        {
            id = tenantId;
            return true;
        }
    }
}
