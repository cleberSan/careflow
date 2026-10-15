using CareFlow.BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.Scheduling.Api.Infrastructure.Persistence;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "Scheduling";

    /// <summary>Papel sem login que recebe os privilégios da API; o login da API é membro dele.</summary>
    public const string ApplicationRole = "scheduling_app";

    /// <summary>Papel do despachante do Outbox e da limpeza do Inbox; não acessa a agenda.</summary>
    public const string RelayRole = "scheduling_relay";

    public const string RelayConnectionStringName = "SchedulingRelay";

    public static IServiceCollection AddSchedulingDatabase(this IServiceCollection services, IConfiguration configuration) =>
        // Sem retry automático no SaveChanges: um commit ambíguo seria repetido. Retentativas seguras
        // são do cliente, protegidas pela Idempotency-Key.
        services.AddTenantDbContext<SchedulingDbContext>(configuration, ConnectionStringName, options => options
            // Violações de constraint são fluxo esperado (horário ocupado) e já são tratadas no handler.
            // Falhas não tratadas continuam registradas como erro pelo middleware de exceções.
            .ConfigureWarnings(warnings => warnings.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));

    public static Task MigrateIfEnabledAsync(this WebApplication app) =>
        app.MigrateIfEnabledAsync<SchedulingDbContext>(
            ConnectionStringName,
            static (options, tenant) => new SchedulingDbContext(options, tenant));
}
