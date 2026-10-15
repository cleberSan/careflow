namespace CareFlow.Consent.Domain.Consents;

/// <summary>
/// O que uma operação fez. <see cref="Unchanged"/> torna concessão e revogação repetidas idempotentes:
/// nada muda, a versão fica igual e nenhum evento novo é publicado.
/// </summary>
public enum ConsentChange
{
    Unchanged = 0,
    Granted,
    Revoked,
}
