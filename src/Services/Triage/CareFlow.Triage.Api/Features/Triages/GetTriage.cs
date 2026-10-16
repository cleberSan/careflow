using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.Triage.Api.Common;
using CareFlow.Triage.Api.Infrastructure.Persistence;
using CareFlow.Triage.Domain.Cases;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Triage.Api.Features.Triages;

internal static class GetTriageEndpoint
{
    /// <summary>
    /// A triagem completa, com a queixa original. Cada leitura entra na trilha do paciente. Triagem de outra clínica
    /// responde 404, como uma inexistente (IDOR): o filtro de clínica e o RLS escondem a linha.
    /// </summary>
    public static async Task<Results<Ok<TriageResponse>, ProblemHttpResult>> HandleAsync(
        Guid id,
        TriageDbContext db,
        TimeProvider time,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var triage = await db.Cases.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (triage is null)
        {
            return TriageErrors.NotFound.ToProblem();
        }

        db.Record(triage, AuditActor.From(httpContext.User), TriageAudit.Read, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(TriageResponse.From(triage));
    }
}
