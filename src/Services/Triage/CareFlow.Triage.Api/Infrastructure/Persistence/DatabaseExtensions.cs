using CareFlow.BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.Triage.Api.Infrastructure.Persistence;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "Triage";

    /// <summary>Papel sem login que recebe os privilégios da API; o login da API é membro dele.</summary>
    public const string ApplicationRole = "triage_app";

    /// <summary>Papel do despachante: só Outbox e Inbox, de todas as clínicas. Não lê triagens nem projeções.</summary>
    public const string RelayRole = "triage_relay";

    public const string RelayConnectionStringName = "TriageRelay";

    public static IServiceCollection AddTriageDatabase(this IServiceCollection services, IConfiguration configuration) =>
        services.AddTenantDbContext<TriageDbContext>(configuration, ConnectionStringName, options => options
            // Conflitos de concorrência são fluxo esperado (revisão simultânea), tratados no código.
            .ConfigureWarnings(warnings => warnings.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));

    public static Task MigrateIfEnabledAsync(this WebApplication app) =>
        app.MigrateIfEnabledAsync<TriageDbContext>(
            ConnectionStringName,
            static (options, tenant) => new TriageDbContext(options, tenant));
}
