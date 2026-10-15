using System.ComponentModel.DataAnnotations;

namespace CareFlow.Notifications.Api.Infrastructure;

/// <summary>Fila de envios (seção <c>Notifications</c>).</summary>
internal sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Liga o despachante dos avisos. Desligado nos testes que chamam o processamento direto.</summary>
    public bool DispatcherEnabled { get; set; } = true;

    /// <summary>Nome usado nas mensagens enquanto a clínica não configura o dela.</summary>
    [Required]
    [StringLength(60, MinimumLength = 2)]
    public string DefaultClinicName { get; set; } = "sua clínica";

    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>Por quanto tempo um aviso fica reservado para uma instância. Depois disso, outra pode retomá-lo.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:30:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Rodadas (todos os canais em ordem) antes de desistir do aviso.</summary>
    [Range(1, 50)]
    public int MaxRounds { get; set; } = 5;

    [Range(typeof(TimeSpan), "00:00:00.010", "01:00:00")]
    public TimeSpan RoundRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    [Range(typeof(TimeSpan), "00:00:00.010", "1.00:00:00")]
    public TimeSpan MaxRoundRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Quanto esperar o evento de consentimento antes de concluir que o paciente não consentiu.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan ConsentGracePeriod { get; set; } = TimeSpan.FromMinutes(5);
}
