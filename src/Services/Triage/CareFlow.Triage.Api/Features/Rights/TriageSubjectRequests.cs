using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Messaging.Rights;
using CareFlow.Contracts.Rights;
using CareFlow.Triage.Api.Common;
using CareFlow.Triage.Api.Features.Triages;
using CareFlow.Triage.Api.Infrastructure.Persistence;
using CareFlow.Triage.Domain.Priorities;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Triage.Api.Features.Rights;

/// <summary>
/// Seção da triagem no pacote do titular: a queixa, o texto mascarado que foi ao modelo, a sugestão, os sinais de
/// alarme e a decisão do profissional, triagem por triagem.
/// </summary>
internal sealed record TriageSection(IReadOnlyList<TriageSection.Item> Triages)
{
    internal sealed record Item(
        Guid Id,
        DateTimeOffset CreatedAt,
        string? Complaint,
        string? SentToModel,
        string? PromptDigest,
        string? ModelName,
        string? SuggestedPriority,
        string Source,
        string? Rationale,
        IReadOnlyList<string> AlarmSigns,
        string Decision,
        string? FinalPriority,
        DateTimeOffset? ReviewedAt);
}

/// <summary>
/// Resposta da triagem a um pedido do titular (ADR-0012). Exportação: as triagens. Eliminação: apaga os textos e
/// mantém a decisão pelo prazo da política; a trilha de auditoria segue com o resumo SHA-256 do que saiu.
/// </summary>
internal sealed class SubjectRequestOpenedConsumer(TriageDbContext db, TriageEraser eraser, TimeProvider time)
    : IIntegrationEventConsumer<SubjectRequestOpened>
{
    public const string ExportAction = "triage.exported";

    public async Task HandleAsync(SubjectRequestOpened @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);
        var now = time.GetUtcNow();

        if (@event.Kind == SubjectRequestKinds.Erasure)
        {
            var lines = await eraser.EraseAsync(context.TenantId, @event.PatientRef, now, cancellationToken).ConfigureAwait(false);
            db.AnswerErasure(context.TenantId, @event.RequestId, SubjectRequestServices.Triage, lines, now);
            return;
        }

        var cases = await db.Cases.AsNoTracking()
            .Where(c => c.PatientRef == @event.PatientRef)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var section = new TriageSection([.. cases.Select(c =>
        {
            var response = TriageResponse.From(c);
            return new TriageSection.Item(
                c.Id, c.CreatedAt, c.Complaint, c.SentToModel, c.PromptDigest, c.ModelName, c.SuggestedPriority?.Slug(), response.Source,
                c.Rationale, c.AlarmSigns, response.Decision, c.FinalPriority?.Slug(), c.ReviewedAt);
        })]);

        db.AnswerExport(context.TenantId, @event, SubjectRequestServices.Triage, section, now);
        db.RecordAccess(context.TenantId, TriageAudit.SourceService, TriageEraser.RightsActor, ExportAction, TriageAudit.ResourceType, @event.PatientRef, now);
    }
}

/// <summary>Reaplica a eliminação depois de restaurar um backup. Idempotente.</summary>
internal sealed class ErasureReappliedConsumer(TriageEraser eraser, TimeProvider time) : IIntegrationEventConsumer<ErasureReapplied>
{
    public async Task HandleAsync(ErasureReapplied @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);
        await eraser.EraseAsync(context.TenantId, @event.PatientRef, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class TriageEraser(TriageDbContext db)
{
    public const string RightsActor = "careflow-rights";
    public const string ErasedAction = "triage.text-erased";

    public async Task<IReadOnlyList<ErasureLine>> EraseAsync(Guid tenantId, Guid patientRef, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cases = await db.Cases
            .Where(c => c.PatientRef == patientRef)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var erased = cases.Count(c => c.EraseText(now));
        if (erased > 0)
        {
            db.RecordAccess(tenantId, TriageAudit.SourceService, RightsActor, ErasedAction, TriageAudit.ResourceType, patientRef, now);
        }

        return
        [
            new(RetentionPolicy.Items.TriageText, RetentionActions.Erased, erased),
            new(RetentionPolicy.Items.TriageDecision, RetentionActions.Retained, cases.Count),
        ];
    }
}
