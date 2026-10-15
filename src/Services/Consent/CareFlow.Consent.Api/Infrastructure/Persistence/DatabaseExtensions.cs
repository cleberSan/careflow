using CareFlow.BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.Consent.Api.Infrastructure.Persistence;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "Consent";

    /// <summary>Papel sem login que recebe os privilégios da API; o login da API é membro dele.</summary>
    public const string ApplicationRole = "consent_app";

    /// <summary>Papel do despachante do Outbox; não acessa a tabela de consentimentos.</summary>
    public const string RelayRole = "consent_relay";

    public const string RelayConnectionStringName = "ConsentRelay";

    public static IServiceCollection AddConsentDatabase(this IServiceCollection services, IConfiguration configuration) =>
        services.AddTenantDbContext<ConsentDbContext>(configuration, ConnectionStringName, options => options
            // Concessão simultânea (índice único) é fluxo esperado, tratado no handler.
            .ConfigureWarnings(warnings => warnings.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));

    public static Task MigrateIfEnabledAsync(this WebApplication app) =>
        app.MigrateIfEnabledAsync<ConsentDbContext>(
            ConnectionStringName,
            static (options, tenant) => new ConsentDbContext(options, tenant));
}
