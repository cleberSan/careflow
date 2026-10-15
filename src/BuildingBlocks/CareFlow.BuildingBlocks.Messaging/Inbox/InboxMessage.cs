namespace CareFlow.BuildingBlocks.Messaging.Inbox;

/// <summary>
/// Registro de que um consumidor já processou uma mensagem. Gravado na mesma transação do efeito do handler:
/// a chave única (consumidor, mensagem) impede que uma reentrega aplique o efeito duas vezes.
/// </summary>
public sealed class InboxMessage
{
    public const int MaxConsumerLength = 128;

    public Guid TenantId { get; init; }

    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }

    public string Type { get; init; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; init; }
}
