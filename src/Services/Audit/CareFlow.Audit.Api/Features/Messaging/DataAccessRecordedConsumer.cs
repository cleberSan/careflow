using CareFlow.Audit.Api.Features.Entries;
using CareFlow.Audit.Domain.Chain;
using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Contracts.Auditing;

namespace CareFlow.Audit.Api.Features.Messaging;

/// <summary>
/// Grava na cadeia os acessos publicados pelos outros serviços. Clínica e identificador vêm do envelope,
/// não do corpo: um serviço não consegue gravar na trilha de outra clínica mudando o JSON.
/// </summary>
internal sealed class DataAccessRecordedConsumer(AuditWriter writer) : IIntegrationEventConsumer<DataAccessRecorded>
{
    public async Task HandleAsync(DataAccessRecorded @event, MessageContext context, CancellationToken cancellationToken)
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
            @event.ResourceId);
        if (auditEvent.IsFailure)
        {
            // Nunca vai passar na validação: vai direto para mensagens mortas, onde alguém investiga.
            throw new PoisonMessageException(auditEvent.Error.Code);
        }

        await writer.AppendAsync(auditEvent.Value, cancellationToken).ConfigureAwait(false);
    }
}
