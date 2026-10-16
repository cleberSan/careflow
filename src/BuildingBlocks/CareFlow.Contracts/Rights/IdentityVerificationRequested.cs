namespace CareFlow.Contracts.Rights;

/// <summary>
/// O serviço de pedidos quer confirmar a identidade do titular: o Notifications manda o código de uso único ao
/// contato do cadastro (que só ele e o cofre leem). O código vai cifrado para a chave pública do Notifications
/// (<see cref="SealedCode"/>, contexto <see cref="SubjectRequestSealing.VerificationCodeContext"/>): Outbox, broker
/// e Inbox guardam mensagens por dias e não podem guardar um código válido em claro.
/// </summary>
public sealed record IdentityVerificationRequested(
    Guid RequestId,
    Guid PatientRef,
    string SealedCode,
    DateTimeOffset ExpiresAt) : IIntegrationEvent
{
    public static string EventType => "rights.identity-verification-requested";
}

/// <summary>Resultado do envio do código: <c>sent</c>, <c>no-contact</c> (nega o pedido) ou <c>failed</c>.</summary>
public sealed record IdentityVerificationDispatched(
    Guid RequestId,
    string Outcome,
    string? Channel,
    DateTimeOffset DispatchedAt) : IIntegrationEvent
{
    public const string Sent = "sent";
    public const string NoContact = "no-contact";
    public const string Failed = "failed";

    public static string EventType => "rights.identity-verification-dispatched";
}
