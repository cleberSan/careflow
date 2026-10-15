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
/// Estado do circuit breaker de cada canal, registrado pelas transições que o próprio Polly anuncia. Por instância do serviço, em memória: cada
/// réplica abre o seu circuito, o que é o comportamento esperado (ADR-0009). Há um circuito por canal e por
/// partição (<see cref="ClinicIsolation"/>): a compartilhada e uma por clínica isolada do demo (ADR-0010).
/// <para>
/// Não usa <see cref="CircuitBreakerStateProvider"/>: ele só serve a um circuit breaker, e o registro de pipelines
/// pode montar o mesmo pipeline duas vezes quando as primeiras chamadas de uma partição chegam juntas (a cópia
/// perdedora é descartada). Com um provider compartilhado, a segunda montagem falhava. O controle manual aceita
/// vários circuitos, então continua compartilhado.
/// </para>
/// </summary>
internal sealed class ChannelCircuits
{
    private readonly ConcurrentDictionary<(Channel, string), ChannelCircuitState> _states = new();
    private readonly ConcurrentDictionary<(Channel, string), CircuitBreakerManualControl> _controls = new();

    /// <summary>Disparado a cada transição do circuito compartilhado (aberto, meio-aberto, fechado).</summary>
    public event Action<Channel, ChannelCircuitState>? StateChanged;

    /// <summary>Disparado a cada transição de qualquer partição.</summary>
    public event Action<Channel, string, ChannelCircuitState>? PartitionStateChanged;

    /// <summary>Controle manual do circuito (isolar ou fechar), para o Painel e para os testes.</summary>
    public CircuitBreakerManualControl ControlFor(Channel channel, string partition = ClinicIsolation.Shared) =>
        _controls.GetOrAdd((channel, partition), _ => new CircuitBreakerManualControl());

    public ChannelCircuitState StateOf(Channel channel, string partition = ClinicIsolation.Shared) =>
        _states.GetValueOrDefault((channel, partition), ChannelCircuitState.Closed);

    /// <summary>Circuitos compartilhados (os do health check).</summary>
    public IReadOnlyDictionary<Channel, ChannelCircuitState> Snapshot() =>
        Enum.GetValues<Channel>().ToDictionary(channel => channel, channel => StateOf(channel));

    /// <summary>Todas as partições já usadas, para as métricas.</summary>
    public IEnumerable<(Channel Channel, string Partition, ChannelCircuitState State)> SnapshotAll() =>
        Enum.GetValues<Channel>().Select(channel => (channel, ClinicIsolation.Shared, StateOf(channel)))
            .Concat(_states.Keys
                .Where(key => key.Item2 != ClinicIsolation.Shared)
                .Select(key => (key.Item1, key.Item2, StateOf(key.Item1, key.Item2))));

    internal void Report(Channel channel, string partition, ChannelCircuitState state)
    {
        _states[(channel, partition)] = state;
        if (partition == ClinicIsolation.Shared)
        {
            StateChanged?.Invoke(channel, state);
        }

        PartitionStateChanged?.Invoke(channel, partition, state);
    }
}
