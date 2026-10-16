using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Messaging.Rights;
using CareFlow.Contracts.Rights;
using CareFlow.Notifications.Api.Common;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Notifications;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Notifications.Api.Features.Rights;

/// <summary>Seção do Notifications no pacote: os avisos ao paciente, sem texto (o texto não é guardado) nem contato.</summary>
internal sealed record NotificationsSection(IReadOnlyList<NotificationsSection.Item> Notifications)
{
    internal sealed record Item(
        Guid AppointmentId,
        string Kind,
        string Status,
        DateTimeOffset AppointmentStartsAt,
        string? SentVia,
        string? NotSentReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);
}

/// <summary>
/// Resposta do Notifications a um pedido do titular (ADR-0012). Exportação: os avisos. Eliminação: os pendentes não
/// saem mais, a referência ao provedor é apagada e o registro dos avisos fica pelo prazo da política.
/// </summary>
internal sealed class SubjectRequestOpenedConsumer(NotificationsDbContext db, NotificationsEraser eraser, TimeProvider time)
    : IIntegrationEventConsumer<SubjectRequestOpened>
{
    public const string ExportAction = "notification.exported";

    public async Task HandleAsync(SubjectRequestOpened @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);
        var now = time.GetUtcNow();

        if (@event.Kind == SubjectRequestKinds.Erasure)
        {
            var lines = await eraser.EraseAsync(@event.PatientRef, now, cancellationToken).ConfigureAwait(false);
            db.AnswerErasure(context.TenantId, @event.RequestId, SubjectRequestServices.Notifications, lines, now);
            return;
        }

        var notifications = await db.Notifications.AsNoTracking()
            .Where(n => n.PatientRef == @event.PatientRef)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var section = new NotificationsSection([.. notifications.Select(n => new NotificationsSection.Item(
            n.AppointmentId, n.Kind.ToString(), n.Status.ToString(), n.AppointmentStartsAt, n.SentVia?.Slug(), n.NotSentReason?.ToString(), n.CreatedAt, n.CompletedAt))]);

        db.AnswerExport(context.TenantId, @event, SubjectRequestServices.Notifications, section, now);
        db.RecordAccess(context.TenantId, NotificationsAudit.SourceService, NotificationsEraser.RightsActor, ExportAction, NotificationsAudit.ResourceType, @event.PatientRef, now);
    }
}

/// <summary>Reaplica a eliminação depois de restaurar um backup. Idempotente.</summary>
internal sealed class ErasureReappliedConsumer(NotificationsEraser eraser, TimeProvider time) : IIntegrationEventConsumer<ErasureReapplied>
{
    public async Task HandleAsync(ErasureReapplied @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);
        await eraser.EraseAsync(@event.PatientRef, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class NotificationsEraser(NotificationsDbContext db)
{
    public const string RightsActor = "careflow-rights";

    public async Task<IReadOnlyList<ErasureLine>> EraseAsync(Guid patientRef, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var notifications = await db.Notifications
            .Where(n => n.PatientRef == patientRef)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int cancelled = 0, references = 0;
        var superseded = new List<Guid>();
        foreach (var notification in notifications)
        {
            var erasure = notification.EraseSubject(now);
            if (erasure.Cancelled)
            {
                cancelled++;
                superseded.Add(notification.Id);
            }

            if (erasure.ProviderReferenceErased)
            {
                references++;
            }
        }

        if (superseded.Count > 0)
        {
            await db.Jobs.Where(j => superseded.Contains(j.NotificationId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return
        [
            new(RetentionPolicy.Items.NotificationsPending, RetentionActions.Cancelled, cancelled),
            new(RetentionPolicy.Items.NotificationsProviderReference, RetentionActions.Erased, references),
            new(RetentionPolicy.Items.NotificationsRecords, RetentionActions.Retained, notifications.Count),
        ];
    }
}
