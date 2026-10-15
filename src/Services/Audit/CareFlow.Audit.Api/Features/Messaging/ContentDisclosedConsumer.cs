using CareFlow.Audit.Api.Features.Entries;
using CareFlow.Audit.Domain.Chain;
using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Contracts.Auditing;

namespace CareFlow.Audit.Api.Features.Messaging;

/// <summary>
/// Grava na cadeia um envio de conteúdo para fora. Igual a <see cref="DataAccessRecordedConsumer"/>, com o resumo
/// SHA-256 do conteúdo coberto pelo hash encadeado. Resumo malformado vai para mensagens mortas.
/// </summary>
internal sealed class ContentDisclosedConsumer(AuditWriter writer) : IIntegrationEventConsumer<ContentDisclosed>
{
    public async Task HandleAsync(ContentDisclosed @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        var auditEvent = AuditEvent.Create(
            context.TenantId,
            context.MessageId,
            @event.OccurredAt,
            @event.SourceService,
            @event.Actor,
            @event.Action,
            @event.ResourceType,
            @event.ResourceId,
            @event.ContentDigest ?? string.Empty);
        if (auditEvent.IsFailure)
        {
            throw new PoisonMessageException(auditEvent.Error.Code);
        }

        await writer.AppendAsync(auditEvent.Value, cancellationToken).ConfigureAwait(false);
    }
}
