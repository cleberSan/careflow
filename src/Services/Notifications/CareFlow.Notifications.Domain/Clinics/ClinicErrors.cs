using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Notifications.Domain.Clinics;

public static class ClinicErrors
{
    public static readonly Error InvalidChannelOrder = Error.Validation(
        "Notificacoes.OrdemDeCanaisInvalida",
        "Informe os canais em ordem de preferência, sem repetir, entre WhatsApp, Sms e Email.");

    public static readonly Error InvalidDisplayName = Error.Validation(
        "Notificacoes.NomeDaClinicaInvalido",
        $"O nome da clínica nas mensagens deve ter de {ClinicSettings.MinDisplayNameLength} a {ClinicSettings.MaxDisplayNameLength} caracteres, só letras, números, espaço e . - &.");

    public static readonly Error InvalidTimeZone = Error.Validation(
        "Notificacoes.FusoHorarioInvalido",
        "Informe um fuso horário IANA válido, por exemplo America/Sao_Paulo.");

    public static readonly Error InvalidReminderLead = Error.Validation(
        "Notificacoes.AntecedenciaInvalida",
        $"A antecedência do lembrete deve ficar entre {ClinicSettings.MinReminderLead.TotalHours:0} e {ClinicSettings.MaxReminderLead.TotalHours:0} horas, em minutos inteiros.");
}
