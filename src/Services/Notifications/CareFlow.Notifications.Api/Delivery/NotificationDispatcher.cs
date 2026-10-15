using System.Diagnostics;

using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Notifications.Api.Common.Observability;
using CareFlow.Notifications.Api.Infrastructure;

using Microsoft.Extensions.Options;

using Npgsql;

namespace CareFlow.Notifications.Api.Delivery;

/// <summary>
/// Scheduler dos avisos: a cada ciclo reserva os vencidos (confirmações e cancelamentos na hora, lembretes na
/// antecedência, retentativas no horário marcado) e processa cada um num escopo com a clínica dele. Um aviso
/// que falhar por exceção continua reservado até o prazo vencer e então volta para a fila.
/// </summary>
internal sealed partial class NotificationDispatcher(
    JobQueue queue,
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<NotificationOptions> options,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    private const int MaxParallelism = 4;

    /// <summary>Um ciclo: reserva um lote e processa. Devolve quantos avisos foram reservados.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var claimed = await queue.ClaimAsync(time.GetUtcNow(), settings.LeaseDuration, settings.BatchSize, cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return 0;
        }

        await Parallel.ForEachAsync(
            claimed,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken },
            async (job, token) => await ProcessOneAsync(job, token).ConfigureAwait(false)).ConfigureAwait(false);
        return claimed.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = settings.PollInterval;
            try
            {
                var claimed = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                failures = 0;
                if (claimed == settings.BatchSize)
                {
                    // Lote cheio: ainda há vencidos, emenda o próximo sem esperar.
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                // Banco indisponível: nada foi reservado ou a reserva vence sozinha.
                LogCycleFailed(logger, ex.GetType().Name);
                delay = TimeSpan.FromMilliseconds(Math.Min(settings.PollInterval.TotalMilliseconds * Math.Pow(2, Math.Min(++failures, 10)), 30_000));
            }

            try
            {
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessOneAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        using var activity = NotificationsDiagnostics.StartDelivery(job.TraceParent);
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(job.TenantId);
            var processor = scope.ServiceProvider.GetRequiredService<DeliveryProcessor>();
            try
            {
                var outcome = await processor.ProcessAsync(job, cancellationToken).ConfigureAwait(false);
                activity?.SetTag("careflow.notification.outcome", outcome.ToString().ToLowerInvariant());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                // A mensagem da exceção não leva dado pessoal: o contato nunca entra em exceções deste serviço.
                LogProcessingFailed(logger, job.NotificationId, ex.GetType().Name, ex);
            }
        }
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning, Message = "Ciclo do despachante de avisos falhou ({ErrorType}); nova tentativa em seguida")]
    private static partial void LogCycleFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Error, Message = "Falha ao processar o aviso {NotificationId}: {ErrorType}; volta para a fila quando a reserva vencer")]
    private static partial void LogProcessingFailed(ILogger logger, Guid notificationId, string errorType, Exception exception);
}
