using System.ComponentModel.DataAnnotations;

using CareFlow.Notifications.Domain.Channels;

namespace CareFlow.Notifications.Api.Channels.Resilience;

internal enum FaultMode
{
    None = 0,

    /// <summary>Atraso antes de chamar o provedor (passa do timeout por tentativa se for maior que ele).</summary>
    Latency,

    /// <summary>O provedor responde 503.</summary>
    Error,

    /// <summary>Indisponibilidade total: a conexão falha.</summary>
    Unavailable,

    /// <summary>
    /// O provedor aceita a mensagem, mas a resposta se perde. É o caso que mais facilmente duplicaria um envio:
    /// a retentativa só é segura porque leva a mesma chave de idempotência.
    /// </summary>
    LostResponse,
}

internal sealed class ChannelFaultOptions
{
    public FaultMode Mode { get; set; }

    /// <summary>Fração das chamadas afetadas (1 = todas).</summary>
    [Range(0.0, 1.0)]
    public double InjectionRate { get; set; } = 1.0;

    [Range(typeof(TimeSpan), "00:00:00", "00:01:00")]
    public TimeSpan Latency { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Falhas injetáveis por canal (seção <c>FaultInjection</c>), para demonstrar fallback e circuit breaker no
/// Painel da fase 6. Desligadas por padrão e só por configuração: sem <see cref="Enabled"/>, nada é injetado,
/// qualquer que seja o modo dos canais. Lidas a cada chamada, então mudar a configuração vale sem reiniciar.
/// </summary>
internal sealed class FaultInjectionOptions
{
    public const string SectionName = "FaultInjection";

    public bool Enabled { get; set; }

    /// <summary>Por canal, pelo nome em minúsculas: <c>whatsapp</c>, <c>sms</c>, <c>email</c>.</summary>
    public Dictionary<string, ChannelFaultOptions> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ChannelFaultOptions? For(Channel channel) =>
        Enabled && Channels.TryGetValue(channel.Slug(), out var fault) && fault.Mode != FaultMode.None ? fault : null;

    public bool IsActive(Channel channel, FaultMode mode) => For(channel)?.Mode == mode;
}
