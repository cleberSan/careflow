namespace CareFlow.Scheduling.Domain.Appointments;

/// <summary>
/// Limites configuráveis de agendamento, aplicados pelo domínio em toda nova reserva.
/// </summary>
public sealed record SchedulingPolicy
{
    public SchedulingPolicy(TimeSpan minimumDuration, TimeSpan maximumDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDuration, minimumDuration);

        MinimumDuration = minimumDuration;
        MaximumDuration = maximumDuration;
    }

    public TimeSpan MinimumDuration { get; }

    public TimeSpan MaximumDuration { get; }
}
