namespace CareFlow.Rights.Domain.Requests;

/// <summary>Direitos do titular atendidos pelo serviço (LGPD art. 18, II, V e VI).</summary>
public enum SubjectRequestKind
{
    Access = 1,
    Portability = 2,
    Erasure = 3,
}
