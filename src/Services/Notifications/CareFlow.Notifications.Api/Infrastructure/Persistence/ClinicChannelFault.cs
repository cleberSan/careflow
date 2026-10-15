using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Domain.Channels;

namespace CareFlow.Notifications.Api.Infrastructure.Persistence;

/// <summary>
/// Falha injetada num canal para uma clínica isolada (demo), com prazo: depois de <see cref="ExpiresAt"/> deixa
/// de valer mesmo que ninguém a remova, então um visitante que sai no meio não deixa o canal derrubado.
/// </summary>
internal sealed class ClinicChannelFault
{
    public Guid TenantId { get; set; }

    public Channel Channel { get; set; }

    public FaultMode Mode { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ChannelFaultOptions ToOptions() => new() { Mode = Mode, InjectionRate = 1.0, Latency = TimeSpan.FromSeconds(5) };
}
