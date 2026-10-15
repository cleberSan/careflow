using System.Collections.Concurrent;
using System.Diagnostics;

using CareFlow.BuildingBlocks.Diagnostics;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Demo.Api.Common;
using CareFlow.Demo.Api.Services;

using Microsoft.Extensions.Options;

namespace CareFlow.Demo.Api.Sessions;

/// <summary>
/// Abre e encerra sessões do Painel. Abrir: partição livre, clínica nova registrada na partição do Notifications,
/// paciente sintético no cofre e consentimentos para agenda, avisos e triagem com IA. Encerrar: apagar a clínica nos cinco
/// serviços (a partição sai junto) e devolver a partição ao pool depois do resfriamento.
/// </summary>
internal sealed partial class SessionLifecycle(
    DemoSessions sessions,
    CareFlowServices services,
    IServiceAuthenticator authenticator,
    IOptions<DemoOptions> options,
    TimeProvider time,
    ILogger<SessionLifecycle> logger)
{
    public static readonly ActivitySource ActivitySource = new("CareFlow.Demo");

    public static readonly Error PoolFull = Error.Failure(
        "Demo.Lotado", "Todas as sessões de demonstração estão em uso. Tente de novo em alguns minutos.");

    private readonly DemoOptions _options = options.Value;
    private readonly ConcurrentQueue<PendingCleanup> _pending = new();

    public async Task<Result<(DemoSession Session, string Cookie)>> OpenAsync(CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("demo open session");
        for (var attempt = 0; attempt < _options.Slots; attempt++)
        {
            if (sessions.ReserveSlot() is not { } slot)
            {
                return Result.Failure<(DemoSession, string)>(PoolFull);
            }

            var clinic = Guid.NewGuid();
            try
            {
                var lease = _options.SessionLifetime + _options.CleanupInterval + TimeSpan.FromMinutes(1);
                if (!await services.LeasePartitionAsync(clinic, slot, lease, cancellationToken).ConfigureAwait(false))
                {
                    // Partição ainda com uma clínica de antes (o Demo reiniciou): espera ela vencer no Notifications.
                    sessions.ReleaseSlot(slot, lease);
                    LogSlotBusy(logger, slot);
                    continue;
                }

                var patient = await services.RegisterPatientAsync(clinic, SyntheticPeople.Create(), cancellationToken).ConfigureAwait(false);
                await services.GrantAsync(clinic, patient, "scheduling", cancellationToken).ConfigureAwait(false);
                await services.GrantAsync(clinic, patient, "notifications", cancellationToken).ConfigureAwait(false);
                await services.GrantAsync(clinic, patient, "ai-triage", cancellationToken).ConfigureAwait(false);

                var (session, cookie) = sessions.Open(clinic, slot);
                session.PatientRef = patient;
                activity?.SetTag("careflow.demo.slot", slot);
                LogOpened(logger, slot, sessions.Active);
                return Result.Success((session, cookie));
            }
            catch (Exception exception) when (exception is UpstreamException or OperationCanceledException)
            {
                // A sessão não abriu: o que já foi criado para a clínica é apagado na próxima rodada de limpeza.
                Schedule(new DemoSession(string.Empty, clinic, slot, time.GetUtcNow(), time.GetUtcNow()), TimeSpan.Zero);
                throw;
            }
        }

        return Result.Failure<(DemoSession, string)>(PoolFull);
    }

    /// <summary>
    /// Encerra a sessão agora (o cookie para de valer) e agenda a limpeza depois de uma folga, para os eventos em
    /// trânsito da clínica chegarem antes de ela ser apagada.
    /// </summary>
    public void End(DemoSession session)
    {
        if (sessions.Close(session))
        {
            Schedule(session, _options.CleanupInterval);
        }
    }

    /// <summary>Uma rodada: sessões vencidas viram limpezas pendentes, e as pendentes que já podem rodar rodam.</summary>
    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        foreach (var expired in sessions.TakeExpired())
        {
            Schedule(expired, TimeSpan.Zero);
        }

        var now = time.GetUtcNow();
        var cleaned = 0;
        var retry = new List<PendingCleanup>();
        while (_pending.TryDequeue(out var pending))
        {
            if (pending.NotBefore > now)
            {
                retry.Add(pending);
                continue;
            }

            if (await TryPurgeAsync(pending.Session, cancellationToken).ConfigureAwait(false))
            {
                cleaned++;
            }
            else
            {
                retry.Add(pending with { NotBefore = now + _options.CleanupInterval, Attempts = pending.Attempts + 1 });
            }
        }

        retry.ForEach(_pending.Enqueue);
        return cleaned;
    }

    /// <summary>Na parada do serviço: tenta limpar tudo o que ainda existe, sem esperar a folga.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        foreach (var session in sessions.TakeAll())
        {
            Schedule(session, TimeSpan.Zero);
        }

        while (_pending.TryDequeue(out var pending))
        {
            await TryPurgeAsync(pending.Session, cancellationToken).ConfigureAwait(false);
        }
    }

    public int PendingCleanups => _pending.Count;

    private void Schedule(DemoSession session, TimeSpan delay) =>
        _pending.Enqueue(new PendingCleanup(session, time.GetUtcNow() + delay, 0));

    private async Task<bool> TryPurgeAsync(DemoSession session, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("demo purge clinic");
        try
        {
            var rows = 0;
            foreach (var service in CareFlowService.Purgeable)
            {
                rows += await services.PurgeAsync(service, session.Clinic, cancellationToken).ConfigureAwait(false);
            }

            authenticator.Forget(session.Clinic);
            sessions.ReleaseSlot(session.Slot);
            var masked = LogMask.Pseudonym(session.Clinic);
            LogPurged(logger, masked, session.Slot, rows);
            return true;
        }
        catch (UpstreamException exception)
        {
            // A partição continua presa até a limpeza dar certo: nenhuma sessão nova herda dados ou falhas.
            activity?.SetStatus(ActivityStatusCode.Error);
            LogPurgeFailed(logger, exception, session.Slot);
            return false;
        }
    }

    private sealed record PendingCleanup(DemoSession Session, DateTimeOffset NotBefore, int Attempts);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Sessão de demonstração aberta na partição {Slot} ({Active} ativas)")]
    private static partial void LogOpened(ILogger logger, int slot, int active);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Partição {Slot} ainda ocupada no Notifications; tentando outra")]
    private static partial void LogSlotBusy(ILogger logger, int slot);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Information, Message = "Clínica de demonstração {Clinic} apagada (partição {Slot}, {Rows} linhas)")]
    private static partial void LogPurged(ILogger logger, string clinic, int slot, int rows);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Warning, Message = "Limpeza da partição {Slot} falhou; nova tentativa na próxima rodada")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception, int slot);
}

/// <summary>Roda a limpeza a cada <see cref="DemoOptions.CleanupInterval"/> e tenta esvaziar tudo na parada.</summary>
internal sealed partial class SessionCleaner(SessionLifecycle lifecycle, IOptions<DemoOptions> options, TimeProvider time, ILogger<SessionCleaner> logger)
    : BackgroundService, IHostedLifecycleService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await lifecycle.CleanupAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogRoundFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada normal.
        }
    }

    // A drenagem roda em StoppingAsync, antes de qualquer serviço hospedado parar: os clientes HTTP e os
    // pipelines de resiliência ainda estão vivos. Em StopAsync o host pode já estar descartando o provedor.
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await lifecycle.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is UpstreamException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            // O que não foi limpo fica para o reaproveitamento da partição: o Notifications zera falhas e circuitos no lease.
            LogRoundFailed(logger, exception);
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 7005, Level = LogLevel.Error, Message = "Rodada de limpeza do demo falhou")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}
