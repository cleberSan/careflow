namespace CareFlow.Notifications.Api.Channels;

/// <summary>
/// Formato HTTP comum dos provedores (o simulado implementa; um adaptador real traduz para a API do provedor).
/// <see cref="To"/> é o contato do paciente: <see cref="ToString"/> é redigido para não vazar em logs.
/// </summary>
internal sealed record ProviderRequest(string To, string Text)
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public override string ToString() => "ProviderRequest { *** }";
}

internal sealed record ProviderResponse(string MessageId, string Status);
