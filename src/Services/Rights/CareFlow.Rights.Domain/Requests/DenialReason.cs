namespace CareFlow.Rights.Domain.Requests;

/// <summary>Motivo de um pedido negado. Nenhum motivo repete dado do titular.</summary>
public enum DenialReason
{
    /// <summary>O cadastro não tem contato para receber o código: a identidade não pode ser confirmada.</summary>
    NoContact = 1,

    /// <summary>Nenhum canal entregou o código.</summary>
    CodeNotDelivered = 2,

    /// <summary>Código expirado ou tentativas esgotadas.</summary>
    IdentityNotConfirmed = 3,

    /// <summary>O cofre não conhece o paciente nesta clínica (ou ele já foi eliminado).</summary>
    SubjectNotFound = 4,
}
