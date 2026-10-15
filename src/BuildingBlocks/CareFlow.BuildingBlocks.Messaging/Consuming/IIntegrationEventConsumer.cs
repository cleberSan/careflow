using CareFlow.Contracts;

namespace CareFlow.BuildingBlocks.Messaging.Consuming;

/// <summary>Dados do envelope da mensagem em processamento.</summary>
public sealed record MessageContext(Guid MessageId, Guid TenantId, string EventType, DateTimeOffset? SentAt);

/// <summary>
/// Trata um evento. Roda dentro da transação do Inbox, num escopo de DI com a clínica da mensagem (RLS):
/// use o DbContext do serviço e não chame <c>SaveChanges</c> fora dela sem necessidade; o processador grava
/// o Inbox e o efeito juntos. Precisa tolerar desordem; duplicatas o Inbox já descarta.
/// </summary>
public interface IIntegrationEventConsumer<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent @event, MessageContext context, CancellationToken cancellationToken);
}
