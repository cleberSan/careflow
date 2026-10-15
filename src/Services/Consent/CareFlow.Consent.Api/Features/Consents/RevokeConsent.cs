using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Consent.Api.Common.Observability;
using CareFlow.Consent.Api.Infrastructure.Persistence;
using CareFlow.Consent.Domain.Consents;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Consent.Api.Features.Consents;

internal sealed partial class RevokeConsentHandler(
    ConsentDbContext db,
    TimeProvider time,
    ConsentMetrics metrics,
    ILogger<RevokeConsentHandler> logger)
{
    /// <summary>Revoga. Revogar de novo é sucesso sem efeito (idempotente) e não publica outro evento.</summary>
    public async Task<Result<ConsentRecord>> HandleAsync(
        Guid patientRef,
        ConsentPurpose purpose,
        string actor,
        CancellationToken cancellationToken)
    {
        var consent = await db.Consents
            .FirstOrDefaultAsync(c => c.PatientRef == patientRef && c.Purpose == purpose, cancellationToken)
            .ConfigureAwait(false);
        if (consent is null)
        {
            return ConsentErrors.NotFound;
        }

        var now = time.GetUtcNow();
        var revocation = consent.Revoke(now);
        if (revocation.IsFailure)
        {
            return revocation.Error;
        }

        if (revocation.Value == ConsentChange.Unchanged)
        {
            return consent;
        }

        ConsentEvents.Revoked(db, consent, actor, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConsentErrors.ConcurrentChange;
        }

        var purposeName = ConsentNames.Of(purpose);
        metrics.Revoked(purposeName);
        LogRevoked(logger, purposeName, consent.Version);
        return consent;
    }

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Consentimento revogado para {Purpose} (versão {Version})")]
    private static partial void LogRevoked(ILogger logger, string purpose, long version);
}

internal static class RevokeConsentEndpoint
{
    public static async Task<Results<Ok<ConsentResponse>, ProblemHttpResult>> HandleAsync(
        Guid patientRef,
        string purpose,
        RevokeConsentHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!ConsentNames.TryParsePurpose(purpose, out var parsedPurpose))
        {
            return ConsentErrors.InvalidPurpose.ToProblem();
        }

        var result = await handler.HandleAsync(patientRef, parsedPurpose, AuditActor.From(context.User), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(ConsentResponse.From(result.Value))
            : result.Error.ToProblem();
    }
}
