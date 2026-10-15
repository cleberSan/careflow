namespace CareFlow.Notifications.Domain.Notifications;

public enum NotificationKind
{
    /// <summary>Consulta marcada.</summary>
    Confirmation = 1,

    /// <summary>Lembrete antes do horário.</summary>
    Reminder,

    /// <summary>Consulta cancelada (a pedido ou por revogação do consentimento para agendamento).</summary>
    Cancellation,
}
