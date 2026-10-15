namespace CareFlow.Notifications.Domain.Consents;

public enum NotificationConsentState
{
    /// <summary>Nenhum evento de consentimento para notificações chegou ainda.</summary>
    Unknown = 0,
    Granted,
    Revoked,
}
