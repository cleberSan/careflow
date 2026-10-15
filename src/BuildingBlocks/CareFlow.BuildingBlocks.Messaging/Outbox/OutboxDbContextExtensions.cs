using CareFlow.Contracts;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.BuildingBlocks.Messaging.Outbox;

public static class OutboxDbContextExtensions
{
    /// <summary>
    /// Coloca o evento no Outbox. Ele só é gravado no próximo <c>SaveChanges</c>, junto com a mudança de
    /// negócio, na mesma transação.
    /// </summary>
    public static void Enqueue<TEvent>(this DbContext db, Guid tenantId, TEvent @event, DateTimeOffset now)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(db);
        db.Set<OutboxMessage>().Add(OutboxMessage.Create(tenantId, @event, now));
    }
}
