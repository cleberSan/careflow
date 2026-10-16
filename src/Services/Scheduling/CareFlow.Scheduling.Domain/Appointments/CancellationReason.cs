namespace CareFlow.Scheduling.Domain.Appointments;

public enum CancellationReason
{
    /// <summary>Cancelada a pedido, pela API.</summary>
    Requested = 1,

    /// <summary>Cancelada pela agenda porque o paciente revogou o consentimento para agendamento.</summary>
    ConsentRevoked,

    /// <summary>Cancelada pela agenda porque o titular pediu a eliminação dos dados (LGPD art. 18, VI).</summary>
    SubjectErasure,
}
