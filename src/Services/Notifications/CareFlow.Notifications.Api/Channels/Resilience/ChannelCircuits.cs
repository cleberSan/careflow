using System.Collections.Concurrent;

using CareFlow.Notifications.Domain.Channels;

using Polly.CircuitBreaker;

namespace CareFlow.Notifications.Api.Channels.Resilience;

/// <summary>Estado do circuit breaker como o Painel mostra: o número é o valor da métrica.</summary>
internal enum ChannelCircuitState
{
    Closed = 0,
    HalfOpen = 1,
    Open = 2,
    Isolated = 3,
}

/// <summary>
/// Estado do circuit breaker de cada canal, lido do próprio Polly. Por instância do serviço, em memória: cada
/// réplica abre o seu circuito, o que é o comportamento esperado (ADR-0009).
/// </summary>
internal sealed class ChannelCircuits
{
    private readonly ConcurrentDictionary<Channel, CircuitBreakerStateProvider> _providers = new();
    private readonly ConcurrentDictionary<Channel, CircuitBreakerManualControl> _controls = new();

    /// <summary>Disparado a cada transição (aberto, meio-aberto, fechado).</summary>
    public event Action<Channel, ChannelCircuitState>? StateChanged;

    public CircuitBreakerStateProvider ProviderFor(Channel channel) => _providers.GetOrAdd(channel, _ => new CircuitBreakerStateProvider());

    /// <summary>Controle manual do circuito (isolar ou fechar), para o Painel da fase 6 e para os testes.</summary>
    public CircuitBreakerManualControl ControlFor(Channel channel) => _controls.GetOrAdd(channel, _ => new CircuitBreakerManualControl());

    public ChannelCircuitState StateOf(Channel channel) =>
        _providers.TryGetValue(channel, out var provider) ? Map(provider.CircuitState) : ChannelCircuitState.Closed;

    public IReadOnlyDictionary<Channel, ChannelCircuitState> Snapshot() =>
        Enum.GetValues<Channel>().ToDictionary(channel => channel, StateOf);

    internal void Report(Channel channel, ChannelCircuitState state) => StateChanged?.Invoke(channel, state);

    private static ChannelCircuitState Map(CircuitState state) => state switch
    {
        CircuitState.Open => ChannelCircuitState.Open,
        CircuitState.HalfOpen => ChannelCircuitState.HalfOpen,
        CircuitState.Isolated => ChannelCircuitState.Isolated,
        _ => ChannelCircuitState.Closed,
    };
}
