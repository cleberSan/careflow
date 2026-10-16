using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Results;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Common;
using CareFlow.Rights.Api.Common.Cryptography;
using CareFlow.Rights.Api.Common.Observability;
using CareFlow.Rights.Api.Infrastructure.Persistence;
using CareFlow.Rights.Domain.Requests;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Rights.Api.Features.Requests;

/// <summary>
/// Recebe o pedido e manda o código de confirmação ao contato do cadastro, pelo Notifications. Nenhum serviço é
/// acionado antes de a identidade ser confirmada. O código sai cifrado para o Notifications e só o HMAC fica aqui.
/// </summary>
internal sealed partial class OpenRequestHandler(
    RightsDbContext db,
    ITenantContext tenant,
    RightsKeyRing keys,
    TimeProvider time,
    RightsMetrics metrics,
    ILogger<OpenRequestHandler> logger)
{
    public async Task<Result<SubjectRequest>> HandleAsync(Guid patientRef, SubjectRequestKind kind, string actor, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var code = RightsKeyRing.NewCode();
        var received = SubjectRequest.Receive(tenant.TenantId, patientRef, kind, id => keys.CodeHash(id, code), now);
        if (received.IsFailure)
        {
            return received.Error;
        }

        var request = received.Value;
        db.Requests.Add(request);
        db.Enqueue(
            request.TenantId,
            new IdentityVerificationRequested(request.Id, patientRef, keys.SealCode(request.Id, code), request.CodeExpiresAt),
            now);
        db.Record(request.TenantId, patientRef, actor, RightsAudit.Received, now);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(SubjectRequestConfiguration.OpenRequestIndex))
        {
            return SubjectRequestErrors.AlreadyOpen;
        }

        var kindName = RequestNames.Of(kind);
        metrics.Received(kindName);
        LogReceived(logger, kindName, request.DueAt);
        return request;
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Pedido do titular recebido ({Kind}), prazo {DueAt:O}")]
    private static partial void LogReceived(ILogger logger, string kind, DateTimeOffset dueAt);
}

internal static class OpenRequestEndpoint
{
    public static async Task<Results<Accepted<RequestResponse>, ProblemHttpResult>> HandleAsync(
        OpenRequestBody body,
        OpenRequestHandler handler,
        HttpContext context,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!RequestNames.TryParseKind(body?.Kind, out var kind))
        {
            return SubjectRequestErrors.InvalidKind.ToProblem();
        }

        var result = await handler.HandleAsync(body!.PatientRef, kind, AuditActor.From(context.User), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var request = result.Value;
        return TypedResults.Accepted(
            $"/v1/subject-requests/{request.Id}",
            RequestView.From(request, [], package: null, receipt: null, time.GetUtcNow()));
    }
}
