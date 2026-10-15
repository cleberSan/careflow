namespace CareFlow.Scheduling.Domain.Patients;

/// <summary>Situação da finalidade "agendamento" do paciente, como a agenda conhece pelos eventos do Consent.</summary>
public enum SchedulingConsent
{
    /// <summary>Nenhum evento de consentimento chegou ainda.</summary>
    Unknown = 0,
    Granted,
    Revoked,
}
