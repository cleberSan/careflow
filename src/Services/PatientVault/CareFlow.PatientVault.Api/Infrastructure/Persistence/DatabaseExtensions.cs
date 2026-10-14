using CareFlow.BuildingBlocks.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.PatientVault.Api.Infrastructure.Persistence;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "PatientVault";

    /// <summary>Papel sem login que recebe os privilégios da API; o login da API é membro dele.</summary>
    public const string ApplicationRole = "vault_app";

    public static IServiceCollection AddVaultDatabase(this IServiceCollection services, IConfiguration configuration) =>
        services.AddTenantDbContext<VaultDbContext>(configuration, ConnectionStringName, options => options
            // CPF duplicado e corrida na criação da chave da clínica são fluxo esperado, tratados nos handlers.
            .ConfigureWarnings(warnings => warnings.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));

    public static Task MigrateIfEnabledAsync(this WebApplication app) =>
        app.MigrateIfEnabledAsync<VaultDbContext>(
            ConnectionStringName,
            static (options, tenant) => new VaultDbContext(options, tenant));
}
