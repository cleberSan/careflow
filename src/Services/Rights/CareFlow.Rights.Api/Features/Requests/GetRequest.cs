using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.Rights.Api.Infrastructure.Persistence;
using CareFlow.Rights.Domain.Requests;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Rights.Api.Features.Requests;

internal static class GetRequestEndpoint
{
    public static async Task<Results<Ok<RequestResponse>, ProblemHttpResult>> HandleAsync(
        Guid requestId,
        RightsDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var request = await db.Requests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return SubjectRequestErrors.NotFound.ToProblem();
        }

        var answers = await db.Answers.AsNoTracking()
            .Where(a => a.RequestId == requestId)
            .Select(a => SubjectRequestAnswer.Create(a.TenantId, a.RequestId, a.Service, a.Outcome, null, a.Erasure, a.AnsweredAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var package = await db.Packages.AsNoTracking()
            .Where(p => p.RequestId == requestId)
            .Select(p => new { p.Size, p.CreatedAt, p.ExpiresAt, p.DestroyedAt, HasContent = p.Content != null })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = time.GetUtcNow();
        var info = package is null
            ? null
            : new PackageInfo(package.HasContent && package.DestroyedAt is null && now < package.ExpiresAt, package.Size, package.CreatedAt, package.ExpiresAt, package.DestroyedAt);
        return TypedResults.Ok(RequestView.From(request, answers, info, RequestView.Receipt(request, answers), now));
    }
}
