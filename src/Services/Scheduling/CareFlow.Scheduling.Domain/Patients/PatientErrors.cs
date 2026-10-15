using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Scheduling.Domain.Patients;

public static class PatientErrors
{
    /// <summary>
    /// O cadastro ainda não chegou à agenda (consistência eventual) ou o pseudônimo não existe. Repetir com a
    /// mesma Idempotency-Key é seguro.
    /// </summary>
    public static readonly Error NotYetKnown = Error.Conflict(
        "Agenda.PacienteAindaNaoConhecido",
        "A agenda ainda não conhece este paciente. Se ele acabou de ser cadastrado, tente de novo em instantes.");

    /// <summary>Sem base legal ativa para agendamento: nunca concedida, ainda não propagada ou revogada.</summary>
    public static readonly Error NoLegalBasis = Error.Conflict(
        "Agenda.PacienteSemBaseLegal",
        "O paciente não tem consentimento ou outra base legal ativa para agendamento.");
}
