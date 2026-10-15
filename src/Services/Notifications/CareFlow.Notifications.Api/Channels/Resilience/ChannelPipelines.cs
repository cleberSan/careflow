using System.Diagnostics;
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
        string partition,
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
                ManualControl = circuits.ControlFor(channel, partition),
                OnOpened = args => Report(circuits, channel, partition, args.IsManual ? ChannelCircuitState.Isolated : ChannelCircuitState.Open),
                OnHalfOpened = _ => Report(circuits, channel, partition, ChannelCircuitState.HalfOpen),
                OnClosed = _ => Report(circuits, channel, partition, ChannelCircuitState.Closed),
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
        // A falha vem da chamada (resolvida pelo gateway para a clínica do aviso) ou, sem ela, da configuração geral.
        ChannelFaultOptions? Resolve(ResilienceContext context) =>
            ChannelCall.Of(context.GetRequestMessage()) is { } call ? call.Fault : faults().For(channel);
        bool Active(ResilienceContext context, FaultMode mode) => Resolve(context)?.Mode == mode;
        double Rate(ResilienceContext context) => Resolve(context)?.InjectionRate ?? 0;

        builder
            .AddChaosLatency(new ChaosLatencyStrategyOptions
            {
                EnabledGenerator = args => ValueTask.FromResult(Active(args.Context, FaultMode.Latency)),
                InjectionRateGenerator = args => ValueTask.FromResult(Rate(args.Context)),
                LatencyGenerator = args => ValueTask.FromResult(Resolve(args.Context)?.Latency ?? TimeSpan.Zero),
            })
            .AddChaosOutcome(new ChaosOutcomeStrategyOptions<HttpResponseMessage>
            {
                EnabledGenerator = args => ValueTask.FromResult(Active(args.Context, FaultMode.Error)),
                InjectionRateGenerator = args => ValueTask.FromResult(Rate(args.Context)),
                OutcomeGenerator = _ => ValueTask.FromResult<Outcome<HttpResponseMessage>?>(
                    Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))),
            })
            .AddChaosFault(new ChaosFaultStrategyOptions
            {
                EnabledGenerator = args => ValueTask.FromResult(Active(args.Context, FaultMode.Unavailable)),
                InjectionRateGenerator = args => ValueTask.FromResult(Rate(args.Context)),
                FaultGenerator = _ => ValueTask.FromResult<Exception?>(
                    new HttpRequestException("Canal indisponível (falha injetada).")),
            });
    }

    private static ValueTask Report(ChannelCircuits circuits, Channel channel, string partition, ChannelCircuitState state)
    {
        circuits.Report(channel, partition, state);
        Activity.Current?.AddEvent(new ActivityEvent(
            "circuit " + state.ToString().ToLowerInvariant(),
            tags: new ActivityTagsCollection { ["channel"] = channel.Slug() }));
        return ValueTask.CompletedTask;
    }
}
