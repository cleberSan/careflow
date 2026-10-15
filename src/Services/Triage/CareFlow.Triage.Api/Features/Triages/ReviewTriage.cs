using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Triage.Api.Common;
using CareFlow.Triage.Api.Common.Observability;
using CareFlow.Triage.Api.Infrastructure.Persistence;
using CareFlow.Triage.Domain.Cases;
using CareFlow.Triage.Domain.Priorities;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Triage.Api.Features.Triages;

internal sealed partial class ReviewTriageHandler(
    TriageDbContext db,
    TimeProvider time,
    TriageMetrics metrics,
    ILogger<ReviewTriageHandler> logger)
{
    /// <summary>
    /// O profissional confirma a sugestão ou corrige a prioridade; o <c>sub</c> dele fica na triagem e na trilha.
    /// Duas revisões simultâneas: a segunda recebe conflito (xmin), nunca sobrescreve a primeira.
    /// </summary>
    public async Task<Result<TriageCase>> HandleAsync(Guid id, ReviewTriageRequest request, string reviewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ReviewDecision decision;
        switch (request.Decision)
        {
            case "confirm":
                decision = ReviewDecision.Confirmed;
                break;
            case "correct":
                decision = ReviewDecision.Corrected;
                break;
            default:
                return TriageErrors.InvalidDecision;
        }

        TriagePriority? priority = null;
        if (request.Priority is not null)
        {
            if (!TriagePriorityNames.TryParse(request.Priority, out var parsed))
            {
                return TriageErrors.InvalidPriority;
            }

            priority = parsed;
        }

        var triage = await db.Cases.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (triage is null)
        {
            return TriageErrors.NotFound;
        }

        var now = time.GetUtcNow();
        var review = triage.Review(reviewer, decision, priority, now);
        if (review.IsFailure)
        {
            return review.Error;
        }

        db.Record(triage, reviewer, TriageAudit.Reviewed(decision), now);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TriageErrors.ConcurrentChange;
        }

        var changed = triage.FinalPriority != triage.SuggestedPriority;
        metrics.Reviewed(decision, changed);
        LogReviewed(logger, triage.Id, decision, changed);
        return triage;
    }

    [LoggerMessage(EventId = 7002, Level = LogLevel.Information, Message = "Triagem {TriageId} revisada: {Decision} (prioridade mudou: {Changed})")]
    private static partial void LogReviewed(ILogger logger, Guid triageId, ReviewDecision decision, bool changed);
}

internal static class ReviewTriageEndpoint
{
    public static async Task<Results<Ok<TriageResponse>, ProblemHttpResult>> HandleAsync(
        Guid id,
        ReviewTriageRequest request,
        ReviewTriageHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, request, AuditActor.From(httpContext.User), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(TriageResponse.From(result.Value))
            : result.Error.ToProblem();
    }
}
