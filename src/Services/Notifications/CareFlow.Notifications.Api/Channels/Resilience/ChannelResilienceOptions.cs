using System.ComponentModel.DataAnnotations;

namespace CareFlow.Notifications.Api.Channels.Resilience;

/// <summary>
/// Pipeline de resiliência de cada canal (seção <c>Notifications:Resilience</c>), da borda para dentro:
/// timeout total, retry exponencial com jitter, circuit breaker e timeout por tentativa.
/// </summary>
internal sealed class ChannelResilienceOptions
{
    public const string SectionName = "Notifications:Resilience";

    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

    [Range(typeof(TimeSpan), "00:00:00.010", "00:05:00")]
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(15);

    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 2;

    [Range(typeof(TimeSpan), "00:00:00", "00:00:30")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Proporção de falhas na janela que abre o circuito.</summary>
    [Range(0.01, 1.0)]
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Tentativas mínimas na janela antes de o circuito poder abrir.</summary>
    [Range(2, 1000)]
    public int MinimumThroughput { get; set; } = 6;

    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Quanto o circuito fica aberto antes de deixar passar uma tentativa de teste (meio-aberto).</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
