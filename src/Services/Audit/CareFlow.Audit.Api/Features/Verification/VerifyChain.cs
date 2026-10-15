using System.Data;

using CareFlow.Audit.Api.Chain;
using CareFlow.Audit.Api.Common.Observability;
using CareFlow.Audit.Api.Infrastructure.Persistence;
using CareFlow.Audit.Domain.Chain;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Audit.Api.Features.Verification;

public sealed record ChainVerificationResponse(
    bool Intact,
    long EntriesVerified,
    long LastSequence,
    string? Failure,
    long? BrokenAtSequence);

internal sealed partial class ChainVerificationHandler(
    AuditDbContext db,
    ChainKeyRing keys,
    AuditMetrics metrics,
    ILogger<ChainVerificationHandler> logger)
{
    /// <summary>
    /// Recalcula a cadeia inteira da clínica em streaming. Roda num snapshot (REPEATABLE READ) para que
    /// entradas gravadas durante a verificação não pareçam uma cabeça divergente.
    /// </summary>
    public async Task<ChainVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var head = await db.ChainHeads
                .AsNoTracking()
                .Select(h => new ChainHead(h.Sequence, h.Hash))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) ?? ChainHead.Genesis;

            var verifier = new ChainVerifier(keys.Find);
            var entries = db.Entries.AsNoTracking().OrderBy(e => e.Sequence).AsAsyncEnumerable();
            await foreach (var row in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var entry = row.ToChainedEntry();
                var accepted = entry is null
                    ? verifier.Reject(row.Sequence)
                    : verifier.Accept(entry);
                if (!accepted)
                {
                    break;
                }
            }

            var result = verifier.Complete(head);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            metrics.Verified(result.IsIntact);
            if (result.IsIntact)
            {
                LogIntact(logger, result.EntriesVerified);
            }
            else
            {
                LogBroken(logger, result.Failure, result.BrokenAtSequence);
            }

            return result;
        }
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Cadeia de auditoria íntegra: {EntriesVerified} entradas verificadas")]
    private static partial void LogIntact(ILogger logger, long entriesVerified);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Critical, Message = "Cadeia de auditoria adulterada: {Failure} na entrada {BrokenAtSequence}")]
    private static partial void LogBroken(ILogger logger, ChainFailure failure, long? brokenAtSequence);
}

internal static class VerifyChainEndpoint
{
    public static async Task<Ok<ChainVerificationResponse>> HandleAsync(
        ChainVerificationHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.VerifyAsync(cancellationToken).ConfigureAwait(false);
        response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new ChainVerificationResponse(
            result.IsIntact,
            result.EntriesVerified,
            result.LastSequence,
            result.IsIntact ? null : result.Failure.ToString(),
            result.BrokenAtSequence));
    }
}
