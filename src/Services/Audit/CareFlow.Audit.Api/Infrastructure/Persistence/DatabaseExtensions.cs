using CareFlow.BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.Audit.Api.Infrastructure.Persistence;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "Audit";

    /// <summary>Papel sem login que recebe os privilégios da API; o login da API é membro dele.</summary>
    public const string ApplicationRole = "audit_app";

    /// <summary>Papel do login de manutenção da mensageria (limpeza do Inbox); não acessa a trilha.</summary>
    public const string RelayRole = "audit_relay";

    public const string RelayConnectionStringName = "AuditRelay";

    public static IServiceCollection AddAuditDatabase(this IServiceCollection services, IConfiguration configuration) =>
        services.AddTenantDbContext<AuditDbContext>(configuration, ConnectionStringName, options => options
            // Evento repetido (índice único da mensagem) é fluxo esperado, tratado no gravador.
            .ConfigureWarnings(warnings => warnings.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));

    public static Task<bool> MigrateIfEnabledAsync(this WebApplication app) =>
        app.MigrateIfEnabledAsync<AuditDbContext>(
            ConnectionStringName,
            static (options, tenant) => new AuditDbContext(options, tenant));
}
