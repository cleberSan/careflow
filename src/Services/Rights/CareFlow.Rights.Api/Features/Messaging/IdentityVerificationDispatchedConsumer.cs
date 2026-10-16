using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Common;
using CareFlow.Rights.Api.Common.Observability;
using CareFlow.Rights.Api.Features.Requests;
using CareFlow.Rights.Api.Infrastructure.Persistence;
using CareFlow.Rights.Domain.Requests;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Rights.Api.Features.Messaging;

/// <summary>
/// O Notifications conta se o código chegou a algum canal. Sem contato no cadastro ou sem entrega, o pedido é
/// negado com o motivo; enviado, o pedido segue aguardando o código.
/// </summary>
internal sealed class IdentityVerificationDispatchedConsumer(RightsDbContext db, RightsMetrics metrics, TimeProvider time)
    : IIntegrationEventConsumer<IdentityVerificationDispatched>
{
    public async Task HandleAsync(IdentityVerificationDispatched @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (@event.Outcome is not (IdentityVerificationDispatched.Sent or IdentityVerificationDispatched.NoContact or IdentityVerificationDispatched.Failed))
        {
            throw new PoisonMessageException("Direitos.ResultadoDeEnvioDesconhecido");
        }

        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == @event.RequestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var wasFinal = request.IsFinal;
        request.CodeDispatched(
            @event.Outcome == IdentityVerificationDispatched.Sent,
            @event.Outcome == IdentityVerificationDispatched.NoContact,
            @event.Channel,
            now);

        if (!wasFinal && request.Status == SubjectRequestStatus.Denied)
        {
            db.Record(request.TenantId, request.PatientRef, RightsAudit.SystemActor, RightsAudit.Denied, now);
            metrics.Finished(RequestNames.Of(request.Kind), RequestNames.Of(request.Status), now - request.ReceivedAt);
        }
    }
}
