using CareFlow.Audit.Api.Features.Entries;
using CareFlow.Audit.Api.Infrastructure.Persistence;
using CareFlow.Audit.Domain.Chain;
using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Rights;
using CareFlow.Contracts.Rights;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Audit.Api.Features.Rights;

/// <summary>Seção da auditoria no pacote: quem acessou ou mudou os dados do paciente, quando e por qual serviço.</summary>
internal sealed record AuditSection(IReadOnlyList<AuditSection.Entry> Entries)
{
    internal sealed record Entry(
        long Sequence,
        DateTimeOffset OccurredAt,
        string SourceService,
        string Actor,
        string Action,
        string? ContentDigest,
        string Hash);
}

/// <summary>
/// Resposta da auditoria a um pedido do titular (ADR-0012). Exportação: a trilha do pseudônimo. Eliminação: nada é
/// apagado (a trilha só tem pseudônimos e resumos, e a cadeia HMAC precisa continuar verificável, ADR-0007); o
/// comprovante diz quantos registros ficam. A própria resposta entra na cadeia.
/// </summary>
internal sealed class SubjectRequestOpenedConsumer(AuditDbContext db, AuditWriter writer, TimeProvider time)
    : IIntegrationEventConsumer<SubjectRequestOpened>
{
    public const string RightsActor = "careflow-rights";
    public const string ExportAction = "audit.exported";
    public const string RetainedAction = "audit.retained-after-erasure";
    public const string ResourceType = "patient";

    public async Task HandleAsync(SubjectRequestOpened @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);
        var now = time.GetUtcNow();

        var trail = db.Entries.AsNoTracking()
            .Where(e => e.ResourceType == ResourceType && e.ResourceId == @event.PatientRef);

        if (@event.Kind == SubjectRequestKinds.Erasure)
        {
            var retained = await trail.CountAsync(cancellationToken).ConfigureAwait(false);
            db.AnswerErasure(
                context.TenantId, @event.RequestId, SubjectRequestServices.Audit, [new(RetentionPolicy.Items.AuditTrail, RetentionActions.Retained, retained)], now);
            await AppendAsync(context, @event.PatientRef, RetainedAction, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        var entries = await trail
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.Sequence, e.OccurredAt, e.SourceService, e.Actor, e.Action, e.ContentDigest, e.Hash })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var section = new AuditSection([.. entries.Select(e => new AuditSection.Entry(
            e.Sequence,
            e.OccurredAt,
            e.SourceService,
            e.Actor,
            e.Action,
            e.ContentDigest is null ? null : Convert.ToHexStringLower(e.ContentDigest),
            Convert.ToHexStringLower(e.Hash)))]);

        db.AnswerExport(context.TenantId, @event, SubjectRequestServices.Audit, section, now);
        await AppendAsync(context, @event.PatientRef, ExportAction, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendAsync(MessageContext context, Guid patientRef, string action, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // O identificador da mensagem do pedido deduplica: uma reentrega não grava de novo.
        var auditEvent = AuditEvent.Create(context.TenantId, context.MessageId, now, "audit", RightsActor, action, ResourceType, patientRef);
        await writer.AppendAsync(auditEvent.Value, cancellationToken).ConfigureAwait(false);
    }
}
