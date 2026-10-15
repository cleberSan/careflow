namespace CareFlow.Notifications.Domain.Channels;

/// <summary>Canal de envio. WhatsApp e SMS usam o telefone; e-mail usa o endereço de e-mail.</summary>
public enum Channel
{
    WhatsApp = 1,
    Sms,
    Email,
}

public static class ChannelExtensions
{
    public static bool UsesPhone(this Channel channel) => channel is Channel.WhatsApp or Channel.Sms;

    /// <summary>Nome estável em minúsculas, usado em métricas, chaves de idempotência e configuração.</summary>
    public static string Slug(this Channel channel) => channel switch
    {
        Channel.WhatsApp => "whatsapp",
        Channel.Sms => "sms",
        Channel.Email => "email",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Canal desconhecido."),
    };
}
