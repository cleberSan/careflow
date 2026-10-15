using System.Globalization;

using CareFlow.Notifications.Domain.Clinics;
using CareFlow.Notifications.Domain.Notifications;

namespace CareFlow.Notifications.Domain.Messages;

/// <summary>
/// Texto das mensagens. Só data, horário e clínica: nada de nome do paciente, profissional, especialidade ou
/// motivo clínico, porque SMS e WhatsApp aparecem na tela bloqueada e o aparelho pode ser compartilhado.
/// </summary>
public static class MessageComposer
{
    public const string Brand = "CareFlow";

    public static string Compose(Notification notification, ClinicSettings clinic)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(clinic);

        var zone = TimeZoneInfo.FindSystemTimeZoneById(clinic.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(notification.AppointmentStartsAt, zone);
        var date = local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var name = clinic.DisplayName;

        return notification.Kind switch
        {
            NotificationKind.Confirmation =>
                $"{Brand}: sua consulta está marcada para {date} às {time} em {name}.",
            NotificationKind.Reminder =>
                $"{Brand}: lembrete da sua consulta em {date} às {time} em {name}.",
            NotificationKind.Cancellation when notification.CancellationCause == CancellationCause.ConsentRevoked =>
                $"{Brand}: sua consulta de {date} às {time} em {name} foi cancelada porque o consentimento para agendamento foi revogado. Para reagendar, fale com a clínica.",
            NotificationKind.Cancellation =>
                $"{Brand}: sua consulta de {date} às {time} em {name} foi cancelada. Para reagendar, fale com a clínica.",
            _ => throw new ArgumentOutOfRangeException(nameof(notification), notification.Kind, "Tipo de aviso desconhecido."),
        };
    }
}
