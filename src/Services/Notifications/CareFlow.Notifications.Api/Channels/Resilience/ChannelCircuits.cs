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
/// réplica abre o seu circuito, o que é o comportamento esperado (ADR-0009). Há um circuito por canal e por
/// partição (<see cref="ClinicIsolation"/>): a compartilhada e uma por clínica isolada do demo (ADR-0010).
/// </summary>
internal sealed class ChannelCircuits
{
    private readonly ConcurrentDictionary<(Channel, string), CircuitBreakerStateProvider> _providers = new();
    private readonly ConcurrentDictionary<(Channel, string), CircuitBreakerManualControl> _controls = new();

    /// <summary>Disparado a cada transição do circuito compartilhado (aberto, meio-aberto, fechado).</summary>
    public event Action<Channel, ChannelCircuitState>? StateChanged;

    /// <summary>Disparado a cada transição de qualquer partição.</summary>
    public event Action<Channel, string, ChannelCircuitState>? PartitionStateChanged;

    public CircuitBreakerStateProvider ProviderFor(Channel channel, string partition = ClinicIsolation.Shared) =>
        _providers.GetOrAdd((channel, partition), _ => new CircuitBreakerStateProvider());

    /// <summary>Controle manual do circuito (isolar ou fechar), para o Painel e para os testes.</summary>
    public CircuitBreakerManualControl ControlFor(Channel channel, string partition = ClinicIsolation.Shared) =>
        _controls.GetOrAdd((channel, partition), _ => new CircuitBreakerManualControl());

    public ChannelCircuitState StateOf(Channel channel, string partition = ClinicIsolation.Shared) =>
        _providers.TryGetValue((channel, partition), out var provider) ? Map(provider.CircuitState) : ChannelCircuitState.Closed;

    /// <summary>Circuitos compartilhados (os do health check).</summary>
    public IReadOnlyDictionary<Channel, ChannelCircuitState> Snapshot() =>
        Enum.GetValues<Channel>().ToDictionary(channel => channel, channel => StateOf(channel));

    /// <summary>Todas as partições já usadas, para as métricas.</summary>
    public IEnumerable<(Channel Channel, string Partition, ChannelCircuitState State)> SnapshotAll() =>
        Enum.GetValues<Channel>().Select(channel => (channel, ClinicIsolation.Shared, StateOf(channel)))
            .Concat(_providers.Keys
                .Where(key => key.Item2 != ClinicIsolation.Shared)
                .Select(key => (key.Item1, key.Item2, StateOf(key.Item1, key.Item2))));

    internal void Report(Channel channel, string partition, ChannelCircuitState state)
    {
        if (partition == ClinicIsolation.Shared)
        {
            StateChanged?.Invoke(channel, state);
        }

        PartitionStateChanged?.Invoke(channel, partition, state);
    }

    private static ChannelCircuitState Map(CircuitState state) => state switch
    {
        CircuitState.Open => ChannelCircuitState.Open,
        CircuitState.HalfOpen => ChannelCircuitState.HalfOpen,
        CircuitState.Isolated => ChannelCircuitState.Isolated,
        _ => ChannelCircuitState.Closed,
    };
}
