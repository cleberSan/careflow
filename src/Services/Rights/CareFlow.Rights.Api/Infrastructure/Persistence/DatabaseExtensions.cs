using CareFlow.BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.Rights.Api.Infrastructure.Persistence;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "Rights";

    /// <summary>Papel sem login que recebe os privilégios da API; o login da API é membro dele.</summary>
    public const string ApplicationRole = "rights_app";

    /// <summary>Papel do despachante do Outbox; não acessa as tabelas de pedidos.</summary>
    public const string RelayRole = "rights_relay";

    public const string RelayConnectionStringName = "RightsRelay";

    public static IServiceCollection AddRightsDatabase(this IServiceCollection services, IConfiguration configuration) =>
        services.AddTenantDbContext<RightsDbContext>(configuration, ConnectionStringName, options => options
            // Pedido duplicado em aberto (índice único) é fluxo esperado, tratado no handler.
            .ConfigureWarnings(warnings => warnings.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));

    public static Task<bool> MigrateIfEnabledAsync(this WebApplication app) =>
        app.MigrateIfEnabledAsync<RightsDbContext>(
            ConnectionStringName,
            static (options, tenant) => new RightsDbContext(options, tenant));
}
