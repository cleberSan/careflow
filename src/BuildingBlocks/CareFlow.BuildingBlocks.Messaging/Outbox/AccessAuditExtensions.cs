using CareFlow.Contracts.Auditing;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.BuildingBlocks.Messaging.Outbox;

public static class AccessAuditExtensions
{
    /// <summary>
    /// Registra um acesso para a trilha de auditoria pelo Outbox: gravado na mesma transação da operação, então
    /// não existe alteração sem registro nem registro de alteração que não aconteceu. Leituras também passam
    /// por aqui e, por isso, gravam uma linha no Outbox.
    /// </summary>
    public static void RecordAccess(
        this DbContext db,
        Guid tenantId,
        string sourceService,
        string actor,
        string action,
        string resourceType,
        Guid resourceId,
        DateTimeOffset now) =>
        db.Enqueue(tenantId, new DataAccessRecorded(sourceService, actor, action, resourceType, resourceId, now), now);
}
