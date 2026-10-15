using System.Net;

using CareFlow.Notifications.Domain.Channels;

using Microsoft.Extensions.Http.Resilience;

using Polly;
using Polly.Simmy;
using Polly.Simmy.Fault;
using Polly.Simmy.Latency;
using Polly.Simmy.Outcomes;

namespace CareFlow.Notifications.Api.Channels.Resilience;

/// <summary>Monta o pipeline Polly v8 de cada canal sobre o Microsoft.Extensions.Http.Resilience.</summary>
internal static class ChannelPipelines
{
    /// <summary>
    /// Da borda para dentro: timeout total, retry exponencial com jitter, circuit breaker do canal, timeout por
    /// tentativa e, por último, as falhas injetáveis. Ficando por dentro, a falha injetada passa pelas mesmas
    /// defesas que uma falha real do provedor: conta para o retry e para o circuit breaker.
    /// </summary>
    public static void ConfigureSend(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        Channel channel,
        ChannelResilienceOptions options,
        ChannelCircuits circuits,
        Func<FaultInjectionOptions> faults)
    {
        builder
            .AddTimeout(options.TotalTimeout)
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryBaseDelay,
            })
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = options.FailureRatio,
                MinimumThroughput = options.MinimumThroughput,
                SamplingDuration = options.SamplingDuration,
                BreakDuration = options.BreakDuration,
                StateProvider = circuits.ProviderFor(channel),
                ManualControl = circuits.ControlFor(channel),
                OnOpened = _ => Report(circuits, channel, ChannelCircuitState.Open),
                OnHalfOpened = _ => Report(circuits, channel, ChannelCircuitState.HalfOpen),
                OnClosed = _ => Report(circuits, channel, ChannelCircuitState.Closed),
            })
            .AddTimeout(options.AttemptTimeout);

        AddFaults(builder, channel, faults);
    }

    /// <summary>
    /// Consulta de status por chave de idempotência: uma tentativa só, com timeout, sem contar para o circuit
    /// breaker do envio. Sofre as mesmas falhas injetadas: canal fora do ar não responde nem consulta.
    /// </summary>
    public static void ConfigureStatus(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        Channel channel,
        ChannelResilienceOptions options,
        Func<FaultInjectionOptions> faults)
    {
        builder.AddTimeout(options.AttemptTimeout);
        AddFaults(builder, channel, faults);
    }

    private static void AddFaults(ResiliencePipelineBuilder<HttpResponseMessage> builder, Channel channel, Func<FaultInjectionOptions> faults)
    {
        builder
            .AddChaosLatency(new ChaosLatencyStrategyOptions
            {
                EnabledGenerator = _ => ValueTask.FromResult(faults().IsActive(channel, FaultMode.Latency)),
                InjectionRateGenerator = _ => ValueTask.FromResult(faults().For(channel)?.InjectionRate ?? 0),
                LatencyGenerator = _ => ValueTask.FromResult(faults().For(channel)?.Latency ?? TimeSpan.Zero),
            })
            .AddChaosOutcome(new ChaosOutcomeStrategyOptions<HttpResponseMessage>
            {
                EnabledGenerator = _ => ValueTask.FromResult(faults().IsActive(channel, FaultMode.Error)),
                InjectionRateGenerator = _ => ValueTask.FromResult(faults().For(channel)?.InjectionRate ?? 0),
                OutcomeGenerator = _ => ValueTask.FromResult<Outcome<HttpResponseMessage>?>(
                    Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))),
            })
            .AddChaosFault(new ChaosFaultStrategyOptions
            {
                EnabledGenerator = _ => ValueTask.FromResult(faults().IsActive(channel, FaultMode.Unavailable)),
                InjectionRateGenerator = _ => ValueTask.FromResult(faults().For(channel)?.InjectionRate ?? 0),
                FaultGenerator = _ => ValueTask.FromResult<Exception?>(
                    new HttpRequestException("Canal indisponível (falha injetada).")),
            });
    }

    private static ValueTask Report(ChannelCircuits circuits, Channel channel, ChannelCircuitState state)
    {
        circuits.Report(channel, state);
        return ValueTask.CompletedTask;
    }
}
