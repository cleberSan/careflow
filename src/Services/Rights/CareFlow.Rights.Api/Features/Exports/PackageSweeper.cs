using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Rights.Api.Common.Observability;
using CareFlow.Rights.Api.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Rights.Api.Features.Exports;

/// <summary>
/// Apaga os pacotes vencidos (7 dias): conteúdo e chave cifrada saem do banco, e os links do pedido também. Lê o
/// índice sem RLS para saber quais clínicas têm pacote vencido e apaga cada um dentro da própria clínica.
/// </summary>
internal sealed partial class PackageSweeper(IServiceScopeFactory scopes, TimeProvider time, RightsMetrics metrics, ILogger<PackageSweeper> logger)
{
    public const int BatchSize = 100;

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        List<PackageExpiration> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RightsDbContext>();
            expired = await db.PackageExpirations.AsNoTracking()
                .Where(e => e.ExpiresAt <= now)
                .OrderBy(e => e.ExpiresAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var expiration in expired)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(expiration.TenantId);
            var db = scope.ServiceProvider.GetRequiredService<RightsDbContext>();

            var package = await db.Packages.FirstOrDefaultAsync(p => p.RequestId == expiration.RequestId, cancellationToken).ConfigureAwait(false);
            package?.Destroy(now);
            await db.DownloadLinks.Where(l => l.RequestId == expiration.RequestId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.PackageExpirations.Remove(expiration);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        metrics.PackagesDestroyed(expired.Count);
        if (expired.Count > 0)
        {
            LogSwept(logger, expired.Count);
        }

        return expired.Count;
    }

    [LoggerMessage(EventId = 8020, Level = LogLevel.Information, Message = "{Count} pacotes vencidos apagados")]
    private static partial void LogSwept(ILogger logger, int count);
}

/// <summary>Roda a limpeza a cada <see cref="Interval"/>. Uma falha registra e tenta de novo na próxima volta.</summary>
internal sealed partial class PackageSweeperService(PackageSweeper sweeper, TimeProvider time, ILogger<PackageSweeperService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await sweeper.SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(EventId = 8021, Level = LogLevel.Warning, Message = "Falha ao apagar pacotes vencidos")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
