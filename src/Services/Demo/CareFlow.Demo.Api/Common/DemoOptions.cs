using System.ComponentModel.DataAnnotations;

namespace CareFlow.Demo.Api.Common;

/// <summary>
/// Configuração do serviço Demo, o backend do Painel de Privacidade e Resiliência (ADR-0010). Cada sessão de
/// visitante ganha uma clínica sintética nova, um paciente sintético e uma partição isolada de circuit breaker
/// no Notifications; tudo some quando a sessão acaba.
/// </summary>
internal sealed class DemoOptions
{
    public const string SectionName = "Demo";

    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Sessões simultâneas, nas partições <see cref="FirstSlot"/> em diante. A última não pode passar de
    /// <c>FaultInjection:IsolatedSlots</c> do Notifications.
    /// </summary>
    [Range(1, 64)]
    public int Slots { get; set; } = 8;

    /// <summary>Primeira partição do pool (duas instâncias do Demo nunca devem dividir partições).</summary>
    [Range(1, 64)]
    public int FirstSlot { get; set; } = 1;

    /// <summary>
    /// Espera antes de uma partição liberada voltar ao pool: maior que a janela de amostragem e a pausa do circuit
    /// breaker, para a sessão seguinte não herdar falhas antigas em nenhuma instância do Notifications.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan SlotCooldown { get; set; } = TimeSpan.FromSeconds(60);

    [Range(typeof(TimeSpan), "00:00:00.050", "00:10:00")]
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(30);

    [Range(1, 20)]
    public int MaxBookingsPerSession { get; set; } = 5;

    /// <summary>Triagens por sessão: cada uma pode virar uma chamada ao modelo, então o teto também limita custo.</summary>
    [Range(1, 20)]
    public int MaxTriagesPerSession { get; set; } = 6;

    /// <summary>Pedidos do titular por sessão (acesso, portabilidade e eliminação, com folga para repetir).</summary>
    [Range(1, 10)]
    public int MaxRightsRequestsPerSession { get; set; } = 4;

    [Range(typeof(TimeSpan), "00:00:10", "00:15:00")]
    public TimeSpan WhatsAppFaultDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Quanto o Demo espera a agenda conhecer o paciente recém-cadastrado (consistência eventual).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan EligibilityWait { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Origens aceitas no cabeçalho <c>Origin</c> (o site). Vazio: só requisições sem <c>Origin</c>.</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    [Required]
    public DemoServiceAddresses Services { get; set; } = new();

    /// <summary>Tempo (traces) e Loki (logs), opcionais: sem eles o Painel ao vivo não mostra traces nem logs.</summary>
    public Uri? TempoBaseAddress { get; set; }

    public Uri? LokiBaseAddress { get; set; }

    [Required]
    public DemoRateLimits RateLimits { get; set; } = new();
}

internal sealed class DemoServiceAddresses
{
    [Required]
    public Uri? Vault { get; set; }

    [Required]
    public Uri? Consent { get; set; }

    [Required]
    public Uri? Scheduling { get; set; }

    [Required]
    public Uri? Notifications { get; set; }

    [Required]
    public Uri? Audit { get; set; }

    [Required]
    public Uri? Triage { get; set; }

    [Required]
    public Uri? Rights { get; set; }
}

internal sealed class DemoRateLimits
{
    /// <summary>Sessões novas por endereço de origem na janela.</summary>
    [Range(1, 1_000)]
    public int SessionsPerWindow { get; set; } = 5;

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan SessionWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Requisições por sessão por minuto (balde de fichas).</summary>
    [Range(1, 10_000)]
    public int RequestsPerMinute { get; set; } = 60;
}
