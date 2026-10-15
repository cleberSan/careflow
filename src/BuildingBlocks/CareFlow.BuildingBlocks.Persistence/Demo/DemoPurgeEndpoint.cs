using CareFlow.BuildingBlocks.Diagnostics;
using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CareFlow.BuildingBlocks.Persistence.Demo;

public sealed record DemoPurgeResult(int Rows);

/// <summary>
/// <c>DELETE /v1/demo/clinic-data</c>: o serviço Demo apaga os dados da clínica sintética de uma sessão do Painel
/// quando ela acaba (ADR-0010). A clínica vem do token, nunca da rota, e o escopo é próprio
/// (<see cref="Scope"/>). Só existe com <c>Demo:PurgeEnabled</c>, que derruba a subida em produção.
/// </summary>
public static partial class DemoPurgeEndpoint
{
    public const string Scope = "demo.purge";
    public const string Route = "/v1/demo/clinic-data";
    public const string EnabledSetting = "Demo:PurgeEnabled";

    private const string LogCategory = "CareFlow.Demo.Purge";

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (!configuration.GetValue<bool>(EnabledSetting))
        {
            return false;
        }

        return environment.IsProduction()
            ? throw new InvalidOperationException($"{EnabledSetting} não pode ser ligado em produção.")
            : true;
    }

    /// <summary>Mapeia a rota quando <c>Demo:PurgeEnabled</c> está ligado.</summary>
    /// <param name="app">A aplicação.</param>
    /// <param name="connectionStringName">Nome da connection string do serviço; a limpeza usa a de migrations.</param>
    /// <param name="policyName">Política que exige a clínica e o escopo <see cref="Scope"/>.</param>
    /// <param name="keep">Tabelas que a limpeza não toca (por exemplo, as só de inserção).</param>
    public static WebApplication MapDemoPurgeIfEnabled(
        this WebApplication app,
        string connectionStringName,
        string policyName,
        params string[] keep)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        if (!IsEnabled(app.Configuration, app.Environment))
        {
            return app;
        }

        var owner = TenantDatabaseExtensions.RequireConnectionString(
            app.Configuration, connectionStringName + TenantDatabaseExtensions.MigrationsSuffix);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);

        app.MapDelete(Route, async Task<Ok<DemoPurgeResult>> (ITenantContext tenant, CancellationToken cancellationToken) =>
            {
                var clinic = tenant.TenantId;
                var rows = await TenantDataPurge.PurgeAsync(owner, clinic, keep, cancellationToken).ConfigureAwait(false);
                var masked = LogMask.Pseudonym(clinic);
                LogPurged(logger, masked, rows);
                return TypedResults.Ok(new DemoPurgeResult(rows));
            })
            .RequireAuthorization(policyName)
            .WithTags("Demo")
            .WithName("PurgeDemoClinicData")
            .WithSummary("Apaga os dados da clínica sintética do token (só no ambiente de demonstração).");

        return app;
    }

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "Dados da clínica de demonstração {Clinic} apagados: {Rows} linhas")]
    private static partial void LogPurged(ILogger logger, string clinic, int rows);
}
