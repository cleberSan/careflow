using CareFlow.Notifications.Domain.Appointments;
using CareFlow.Notifications.Domain.Consents;

namespace CareFlow.Notifications.Domain.Notifications;

public enum DeliveryDecision
{
    /// <summary>Pode enviar.</summary>
    Send = 1,

    /// <summary>Já não está pendente: nada a fazer.</summary>
    AlreadyClosed,

    /// <summary>A consulta foi cancelada; confirmação e lembrete perdem o sentido.</summary>
    Supersede,

    /// <summary>A consulta já começou.</summary>
    Expire,

    /// <summary>O paciente não consentiu (ou revogou) receber notificações.</summary>
    BlockNoConsent,

    /// <summary>O consentimento ainda não chegou por evento; espera um pouco antes de concluir que não existe.</summary>
    WaitForConsent,
}

/// <summary>
/// Regras avaliadas na hora do envio, não na criação: entre uma e outra o paciente pode revogar o consentimento
/// e a consulta pode ser cancelada.
/// </summary>
public static class DeliveryRules
{
    public static DeliveryDecision Evaluate(
        Notification notification,
        AppointmentTrack? appointment,
        NotificationConsent? consent,
        TimeSpan consentGracePeriod,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!notification.IsPending)
        {
            return DeliveryDecision.AlreadyClosed;
        }

        if (notification.Kind != NotificationKind.Cancellation && appointment?.State == AppointmentState.Cancelled)
        {
            return DeliveryDecision.Supersede;
        }

        if (notification.AppointmentStartsAt <= now)
        {
            return DeliveryDecision.Expire;
        }

        return consent?.State switch
        {
            NotificationConsentState.Granted => DeliveryDecision.Send,
            NotificationConsentState.Revoked => DeliveryDecision.BlockNoConsent,

            // Consentimento e consulta chegam por filas diferentes. Sem notícia do Consent, espera o período de
            // carência antes de registrar o aviso como não enviado.
            _ => now - notification.CreatedAt < consentGracePeriod
                ? DeliveryDecision.WaitForConsent
                : DeliveryDecision.BlockNoConsent,
        };
    }
}
