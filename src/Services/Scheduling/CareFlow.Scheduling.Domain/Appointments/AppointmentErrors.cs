using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Scheduling.Domain.Appointments;

public static class AppointmentErrors
{
    public static readonly Error MissingIdentifier =
        Error.Validation("Agenda.IdentificadorAusente", "Profissional, paciente e clínica são obrigatórios.");

    public static readonly Error InvalidInterval =
        Error.Validation("Agenda.IntervaloInvalido", "O término precisa ser posterior ao início.");

    public static readonly Error NotAlignedToMinute =
        Error.Validation("Agenda.HorarioNaoAlinhado", "Início e término devem estar em minutos exatos, sem segundos.");

    public static readonly Error StartsInThePast =
        Error.Validation("Agenda.InicioNoPassado", "Não é possível agendar no passado.");

    public static readonly Error SlotUnavailable =
        Error.Conflict("Agenda.HorarioIndisponivel", "O profissional já tem uma consulta nesse horário.");

    public static readonly Error NotFound =
        Error.NotFound("Agenda.ConsultaNaoEncontrada", "Consulta não encontrada.");

    public static readonly Error AlreadyCancelled =
        Error.Conflict("Agenda.ConsultaJaCancelada", "A consulta já está cancelada.");

    public static readonly Error AlreadyStarted =
        Error.Conflict("Agenda.ConsultaJaIniciada", "Não é possível cancelar uma consulta que já começou.");

    public static readonly Error ConcurrentChange =
        Error.Conflict("Agenda.AlteracaoConcorrente", "A consulta foi alterada por outra requisição. Tente novamente.");

    public static Error DurationOutOfRange(SchedulingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return Error.Validation(
            "Agenda.DuracaoForaDoLimite",
            $"A duração deve ficar entre {policy.MinimumDuration.TotalMinutes:0} e {policy.MaximumDuration.TotalMinutes:0} minutos.");
    }
}
