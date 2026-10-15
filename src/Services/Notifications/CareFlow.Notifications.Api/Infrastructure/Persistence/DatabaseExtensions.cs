using CareFlow.BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.Notifications.Api.Infrastructure.Persistence;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "Notifications";

    /// <summary>Papel sem login que recebe os privilégios da API; o login da API é membro dele.</summary>
    public const string ApplicationRole = "notifications_app";

    /// <summary>
    /// Papel do despachante: Outbox, Inbox e a fila de envios (só identificadores e horários) de todas as
    /// clínicas. Não lê avisos, projeções nem configurações.
    /// </summary>
    public const string RelayRole = "notifications_relay";

    public const string RelayConnectionStringName = "NotificationsRelay";

    public static IServiceCollection AddNotificationsDatabase(this IServiceCollection services, IConfiguration configuration) =>
        services.AddTenantDbContext<NotificationsDbContext>(configuration, ConnectionStringName, options => options
            // Conflitos de concorrência e de unicidade são fluxo esperado, tratados no código.
            .ConfigureWarnings(warnings => warnings.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));

    public static Task MigrateIfEnabledAsync(this WebApplication app) =>
        app.MigrateIfEnabledAsync<NotificationsDbContext>(
            ConnectionStringName,
            static (options, tenant) => new NotificationsDbContext(options, tenant));
}
