using System.Collections.Frozen;

using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Channels.Resilience;

/// <summary>
/// Em qual partição de circuit breaker uma clínica cai. As clínicas de produção dividem um circuito por canal
/// (um provedor fora do ar está fora para todos). As clínicas isoladas, do pool do demo, têm o próprio: a falha que
/// um visitante injeta abre só o circuito da clínica dele. O rótulo é o índice no pool, nunca o id da clínica.
/// </summary>
internal sealed class ClinicIsolation
{
    public const string Shared = "shared";

    private readonly FrozenDictionary<Guid, string> _partitions;

    public ClinicIsolation(IOptions<FaultInjectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _partitions = options.Value.IsolatedClinics
            .Distinct()
            .Select((clinic, index) => (clinic, label: $"isolated-{index + 1:00}"))
            .ToFrozenDictionary(entry => entry.clinic, entry => entry.label);
        OverridesEnabled = options.Value.ClinicOverridesEnabled;
    }

    public bool OverridesEnabled { get; }

    public IEnumerable<string> IsolatedPartitions => _partitions.Values;

    public bool IsIsolated(Guid clinic) => _partitions.ContainsKey(clinic);

    public string PartitionFor(Guid clinic) => _partitions.TryGetValue(clinic, out var label) ? label : Shared;
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
