using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Common;
using CareFlow.Rights.Api.Infrastructure.Persistence;
using CareFlow.Rights.Domain.Requests;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Rights.Api.Features.Requests;

/// <summary>
/// Depois de restaurar um backup, a clínica reaplica as eliminações concluídas: o banco restaurado pode ter de volta a
/// chave de um paciente ou o texto de uma triagem. Cada serviço trata de forma idempotente (ADR-0012).
/// </summary>
internal static class ReplayErasureEndpoint
{
    public static async Task<Results<Accepted, ProblemHttpResult>> HandleAsync(
        Guid requestId,
        RightsDbContext db,
        HttpContext context,
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

        if (request is not { Kind: SubjectRequestKind.Erasure, Status: SubjectRequestStatus.Completed, CompletedAt: { } erasedAt })
        {
            return SubjectRequestErrors.NotACompletedErasure.ToProblem();
        }

        var now = time.GetUtcNow();
        db.Enqueue(
            request.TenantId,
            new ErasureReapplied(request.Id, request.PatientRef, request.PolicyVersion ?? RetentionPolicy.Version, erasedAt),
            now);
        db.Record(request.TenantId, request.PatientRef, AuditActor.From(context.User), RightsAudit.ErasureReapplied, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted($"/v1/subject-requests/{request.Id}");
    }
}
