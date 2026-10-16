using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Messaging.Rights;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.Consent.Api.Common.Observability;
using CareFlow.Consent.Api.Features.Consents;
using CareFlow.Consent.Api.Infrastructure.Persistence;
using CareFlow.Consent.Domain.Consents;
using CareFlow.Contracts.Rights;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Consent.Api.Features.Rights;

/// <summary>
/// Resposta do Consent a um pedido do titular (ADR-0012). Exportação: consentimentos e histórico. Eliminação: revoga o
/// que é revogável (os outros serviços param pelo evento de sempre), recusa concessões futuras ao pseudônimo e
/// mantém o histórico pelo prazo da política, porque é a prova do consentimento.
/// </summary>
internal sealed class SubjectRequestOpenedConsumer(ConsentDbContext db, ConsentEraser eraser, TimeProvider time)
    : IIntegrationEventConsumer<SubjectRequestOpened>
{
    public const string ExportAction = "consent.exported";

    public async Task HandleAsync(SubjectRequestOpened @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);
        var now = time.GetUtcNow();

        if (@event.Kind == SubjectRequestKinds.Erasure)
        {
            var lines = await eraser.EraseAsync(context.TenantId, @event.RequestId, @event.PatientRef, now, cancellationToken).ConfigureAwait(false);
            db.AnswerErasure(context.TenantId, @event.RequestId, SubjectRequestServices.Consent, lines, now);
            return;
        }

        var consents = await db.Consents.AsNoTracking()
            .Where(c => c.PatientRef == @event.PatientRef)
            .OrderBy(c => c.Purpose)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var history = await db.History.AsNoTracking()
            .Where(h => h.PatientRef == @event.PatientRef)
            .OrderBy(h => h.OccurredAt).ThenBy(h => h.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var section = new ConsentSection(
            [.. consents.Select(c => new ConsentSection.Current(
                ConsentNames.Of(c.Purpose), ConsentNames.Of(c.LegalBasis), c.TermVersion, c.Status.ToString(), c.GrantedAt, c.RevokedAt, c.Version))],
            [.. history.Select(h => new ConsentSection.HistoryItem(
                ConsentNames.Of(h.Purpose), ConsentNames.Of(h.LegalBasis), h.TermVersion, h.Change.ToString(), h.Version, h.OccurredAt))]);

        db.AnswerExport(context.TenantId, @event, SubjectRequestServices.Consent, section, now);
        db.RecordAccess(context.TenantId, ConsentEvents.SourceService, ConsentEraser.RightsActor, ExportAction, ConsentEvents.ResourceType, @event.PatientRef, now);
    }
}

/// <summary>Reaplica a eliminação depois de restaurar um backup. Idempotente.</summary>
internal sealed class ErasureReappliedConsumer(ConsentEraser eraser, TimeProvider time) : IIntegrationEventConsumer<ErasureReapplied>
{
    public async Task HandleAsync(ErasureReapplied @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);
        await eraser.EraseAsync(context.TenantId, @event.RequestId, @event.PatientRef, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }
}

internal sealed partial class ConsentEraser(ConsentDbContext db, ConsentMetrics metrics, ILogger<ConsentEraser> logger)
{
    public const string RightsActor = "careflow-rights";

    public async Task<IReadOnlyList<ErasureLine>> EraseAsync(
        Guid tenantId, Guid requestId, Guid patientRef, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await SubjectLock.AcquireAsync(db, patientRef, cancellationToken).ConfigureAwait(false);

        if (!await db.ErasedSubjects.AnyAsync(e => e.PatientRef == patientRef, cancellationToken).ConfigureAwait(false))
        {
            db.ErasedSubjects.Add(new ErasedSubject { TenantId = tenantId, PatientRef = patientRef, RequestId = requestId, ErasedAt = now });
        }

        var consents = await db.Consents
            .Where(c => c.PatientRef == patientRef)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var revoked = 0;
        foreach (var consent in consents.Where(c => c.Status == ConsentStatus.Granted && c.LegalBasis.IsRevocable()))
        {
            if (consent.Revoke(now) is { IsSuccess: true, Value: ConsentChange.Revoked })
            {
                revoked++;
                ConsentEvents.Revoked(db, consent, RightsActor, now);
                metrics.Revoked(ConsentNames.Of(consent.Purpose));
            }
        }

        var history = await db.History.CountAsync(h => h.PatientRef == patientRef, cancellationToken).ConfigureAwait(false) + revoked;
        LogErased(logger, revoked);
        return [new(RetentionPolicy.Items.ConsentHistory, RetentionActions.Retained, history)];
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information, Message = "Eliminação do titular no Consent: {Revoked} consentimento(s) revogado(s)")]
    private static partial void LogErased(ILogger logger, int revoked);
}
