namespace CareFlow.Notifications.Domain.Notifications;

public enum NotificationStatus
{
    /// <summary>Na fila, esperando a hora de envio ou uma nova tentativa.</summary>
    Pending = 1,

    /// <summary>Um provedor aceitou a mensagem.</summary>
    Sent,

    /// <summary>Não enviada por regra: sem consentimento ou sem contato. O motivo fica em <see cref="NotSentReason"/>.</summary>
    NotSent,

    /// <summary>Perdeu o sentido antes do envio: a consulta foi cancelada (confirmação e lembrete).</summary>
    Superseded,

    /// <summary>A consulta começou antes de o aviso sair.</summary>
    Expired,

    /// <summary>Todos os canais falharam em todas as rodadas.</summary>
    Failed,
}

public enum NotSentReason
{
    NoConsent = 1,
    NoContact,
}
