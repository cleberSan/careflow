using System.Security.Cryptography;

using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Common;
using CareFlow.Rights.Api.Common.Cryptography;
using CareFlow.Rights.Api.Common.Observability;
using CareFlow.Rights.Api.Infrastructure.Persistence;
using CareFlow.Rights.Domain.Requests;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Rights.Api.Features.Requests;

internal sealed record IdentityOutcome(SubjectRequest Request, IdentityCheck Check);

/// <summary>
/// Confere o código. Confirmado, o pedido vai para todos os serviços: em acesso e portabilidade com a chave pública
/// de um par gerado só para este pedido (a privada fica cifrada aqui); em eliminação com a versão da política.
/// </summary>
internal sealed partial class ConfirmIdentityHandler(
    RightsDbContext db,
    RightsKeyRing keys,
    TimeProvider time,
    RightsMetrics metrics,
    ILogger<ConfirmIdentityHandler> logger)
{
    public async Task<Result<IdentityOutcome>> HandleAsync(Guid requestId, string code, string actor, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return SubjectRequestErrors.NotFound;
        }

        var check = request.ConfirmIdentity(keys.CodeHash(request.Id, code), now);
        if (check.IsFailure)
        {
            return check.Error;
        }

        if (check.Value == IdentityCheck.Confirmed)
        {
            Open(request, now);
            db.Record(request.TenantId, request.PatientRef, actor, RightsAudit.IdentityConfirmed, now);
        }
        else
        {
            db.Record(request.TenantId, request.PatientRef, actor, RightsAudit.IdentityRejected, now);
            if (request.Status == SubjectRequestStatus.Denied)
            {
                db.Record(request.TenantId, request.PatientRef, RightsAudit.SystemActor, RightsAudit.Denied, now);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SubjectRequestErrors.ConcurrentChange;
        }

        var result = check.Value.ToString();
        metrics.IdentityChecked(result);
        if (request.Status == SubjectRequestStatus.Denied)
        {
            metrics.Finished(RequestNames.Of(request.Kind), RequestNames.Of(request.Status), now - request.ReceivedAt);
        }

        LogChecked(logger, result, request.RemainingAttempts);
        return new IdentityOutcome(request, check.Value);
    }

    private void Open(SubjectRequest request, DateTimeOffset now)
    {
        string? publicKey = null;
        if (request.IsExport)
        {
            using var recipient = SealedBox.CreateKeyPair();
            publicKey = SealedBox.ExportPublicKey(recipient);
            var privateKey = recipient.ExportPkcs8PrivateKey();
            try
            {
                request.AttachRecipientKey(keys.Wrap(privateKey, RightsKeyRing.RecipientKeyContext(request.Id)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
        else
        {
            request.ApplyPolicy(RetentionPolicy.Version);
        }

        db.Enqueue(
            request.TenantId,
            new SubjectRequestOpened(request.Id, request.PatientRef, RequestNames.Of(request.Kind), publicKey, RetentionPolicy.Version, now),
            now);
    }

    [LoggerMessage(EventId = 8002, Level = LogLevel.Information, Message = "Confirmação de identidade: {Result} ({Remaining} tentativas restantes)")]
    private static partial void LogChecked(ILogger logger, string result, int remaining);
}

internal static class ConfirmIdentityEndpoint
{
    public static async Task<Results<Ok<RequestResponse>, ProblemHttpResult>> HandleAsync(
        Guid requestId,
        ConfirmIdentityBody body,
        ConfirmIdentityHandler handler,
        HttpContext context,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // Formato errado não conta tentativa: não ajuda quem tenta adivinhar e evita punir um erro de digitação.
        if (body?.Code is not { Length: 6 } code || !code.All(char.IsAsciiDigit))
        {
            return SubjectRequestErrors.InvalidCode.ToProblem();
        }

        var result = await handler.HandleAsync(requestId, code, AuditActor.From(context.User), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var (request, check) = result.Value;
        return check switch
        {
            IdentityCheck.Confirmed => TypedResults.Ok(RequestView.From(request, [], package: null, receipt: null, time.GetUtcNow())),
            IdentityCheck.WrongCode => Rejected(SubjectRequestErrors.InvalidCode, request.RemainingAttempts),
            IdentityCheck.Expired => Rejected(SubjectRequestErrors.CodeExpired, 0),
            _ => Rejected(SubjectRequestErrors.AttemptsExhausted, 0),
        };
    }

    private static ProblemHttpResult Rejected(Error error, int remainingAttempts)
    {
        var status = ErrorHttpExtensions.StatusCodeFor(error.Type);
        return TypedResults.Problem(
            statusCode: status,
            title: Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status),
            detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["remainingAttempts"] = remainingAttempts });
    }
}
