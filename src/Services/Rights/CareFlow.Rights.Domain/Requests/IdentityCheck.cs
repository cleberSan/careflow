namespace CareFlow.Rights.Domain.Requests;

/// <summary>Resultado de uma tentativa de confirmar a identidade com o código.</summary>
public enum IdentityCheck
{
    Confirmed = 1,
    WrongCode = 2,
    Expired = 3,
    AttemptsExhausted = 4,
}
