using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.Scheduling.Api.Infrastructure.Persistence;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "Scheduling";

    public static IServiceCollection AddSchedulingDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Configure a connection string '{ConnectionStringName}' (variável ConnectionStrings__{ConnectionStringName}).");
        }

        // Sem retry automático no SaveChanges: um commit ambíguo seria repetido. Retentativas seguras
        // são do cliente, protegidas pela Idempotency-Key.
        services.AddDbContext<SchedulingDbContext>(options => options
            .UseNpgsql(connectionString)
            // Violações de constraint são fluxo esperado (horário ocupado) e já são tratadas no handler.
            // Falhas não tratadas continuam registradas como erro pelo middleware de exceções.
            .ConfigureWarnings(warnings => warnings.Log(
                (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug))));
        return services;
    }

    /// <summary>
    /// Aplica migrations na subida apenas quando habilitado (desenvolvimento e testes).
    /// Em produção, migrations rodam como etapa do pipeline de deploy.
    /// </summary>
    public static async Task MigrateIfEnabledAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }
}
