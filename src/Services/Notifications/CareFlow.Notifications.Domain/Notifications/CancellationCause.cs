namespace CareFlow.Notifications.Domain.Notifications;

/// <summary>Por que a consulta foi cancelada; muda o texto do aviso.</summary>
public enum CancellationCause
{
    Requested = 1,
    ConsentRevoked,
}
