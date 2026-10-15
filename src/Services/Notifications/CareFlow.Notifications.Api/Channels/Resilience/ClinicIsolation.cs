using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Channels.Resilience;

/// <summary>
/// Em qual partição de circuit breaker uma clínica cai. As clínicas de produção dividem um circuito por canal
/// (um provedor fora do ar está fora para todos). Uma clínica sintética do Painel, registrada numa partição isolada
/// com prazo (<see cref="Infrastructure.Persistence.ClinicPartition"/>), tem o próprio: a falha que um visitante
/// injeta abre só o circuito da sessão dele. O rótulo é o número da partição, nunca o id da clínica.
/// </summary>
internal sealed class ClinicIsolation(IOptions<FaultInjectionOptions> options)
{
    public const string Shared = "shared";

    public const int MinLeaseSeconds = 60;
    public const int MaxLeaseSeconds = 3_600;

    private readonly FaultInjectionOptions _options = options.Value;

    /// <summary>Partições isoladas e a API de falhas por clínica: só fora de produção e com partições configuradas.</summary>
    public bool OverridesEnabled => _options.ClinicOverridesEnabled && _options.IsolatedSlots > 0;

    public IEnumerable<string> IsolatedPartitions => Enumerable.Range(1, _options.IsolatedSlots).Select(Label);

    public bool IsValidSlot(int slot) => slot >= 1 && slot <= _options.IsolatedSlots;

    public static string Label(int slot) => $"isolated-{slot:00}";
}

/// <summary>
/// O que o gateway resolveu para uma chamada a um canal: em qual circuito ela conta e qual falha (se alguma) vale
/// para ela. Viaja no <see cref="HttpRequestMessage.Options"/> até o pipeline e o provedor simulado.
/// </summary>
internal sealed record ChannelCall(string Partition, ChannelFaultOptions? Fault)
{
    public static readonly HttpRequestOptionsKey<ChannelCall> Key = new("careflow.channel-call");

    public static ChannelCall? Of(HttpRequestMessage? request) =>
        request is not null && request.Options.TryGetValue(Key, out var call) ? call : null;
}
