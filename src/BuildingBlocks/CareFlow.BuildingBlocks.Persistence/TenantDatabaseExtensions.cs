using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;
using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CareFlow.BuildingBlocks.Persistence;

public static class TenantDatabaseExtensions
{
    /// <summary>Sufixo da connection string usada só para migrations (papel dono do schema).</summary>
    public const string MigrationsSuffix = "Migrations";

    /// <summary>
    /// Registra o DbContext com a clínica gravada na sessão (RLS) e os health checks de prontidão:
    /// conexão com o banco e conexão com papel que respeita RLS.
    /// </summary>
    public static IServiceCollection AddTenantDbContext<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName,
        Action<DbContextOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = RequireConnectionString(configuration, connectionStringName);

        services.TryAddScoped<TenantSessionInterceptor>();
        services.AddDbContext<TContext>((provider, options) =>
        {
            // Interceptores extras registrados como IInterceptor (por exemplo, o sinal do Outbox) entram em todo contexto.
            options
                .UseNpgsql(connectionString)
                .AddInterceptors(provider.GetRequiredService<TenantSessionInterceptor>())
                .AddInterceptors(provider.GetServices<IInterceptor>());
            configure?.Invoke(options);
        });

        services.AddHealthChecks()
            .AddDbContextCheck<TContext>(tags: ["ready"])
            .AddCheck<RowLevelSecurityHealthCheck<TContext>>("row-level-security", tags: ["ready"]);

        return services;
    }

    public const string MigrateOnStartupSetting = "Database:ApplyMigrationsOnStartup";
    public const string MigrateAndExitSetting = "Database:MigrateAndExit";

    /// <summary>
    /// Aplica migrations com a connection string de migrations em dois casos: <c>Database:ApplyMigrationsOnStartup</c>
    /// (desenvolvimento e testes, a API segue subindo) ou <c>Database:MigrateAndExit</c> (etapa do deploy, num
    /// container de uma execução só). No deploy a API nunca recebe a senha do papel dono do schema (ADR-0013).
    /// </summary>
    /// <returns>Verdadeiro quando o processo deve terminar sem subir a API.</returns>
    public static async Task<bool> MigrateIfEnabledAsync<TContext>(
        this WebApplication app,
        string connectionStringName,
        Func<DbContextOptions<TContext>, ITenantContext, TContext> factory)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(factory);

        var exitAfter = app.Configuration.GetValue<bool>(MigrateAndExitSetting);
        if (!exitAfter && !app.Configuration.GetValue<bool>(MigrateOnStartupSetting))
        {
            return false;
        }

        var connectionString = RequireConnectionString(app.Configuration, connectionStringName + MigrationsSuffix);
        var options = new DbContextOptionsBuilder<TContext>().UseNpgsql(connectionString).Options;

        var db = factory(options, NoTenantContext.Instance);
        await using (db.ConfigureAwait(false))
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
        }

        return exitAfter;
    }

    internal static string RequireConnectionString(IConfiguration configuration, string name)
    {
        var connectionString = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Configure a connection string '{name}' (variável ConnectionStrings__{name}).");
        }

        return connectionString;
    }
}
