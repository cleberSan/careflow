using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Consent.Api.Infrastructure.Persistence;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Consent.Api.Features.Consents;

internal static class ListConsentsEndpoint
{
    /// <summary>Situação de todas as finalidades do paciente. A consulta em si também entra na trilha de auditoria.</summary>
    public static async Task<Ok<ConsentResponse[]>> HandleAsync(
        Guid patientRef,
        ConsentDbContext db,
        ITenantContext tenant,
        TimeProvider time,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var consents = await db.Consents
            .AsNoTracking()
            .Where(c => c.PatientRef == patientRef)
            .OrderBy(c => c.Purpose)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        db.RecordAccess(
            tenant.TenantId, ConsentEvents.SourceService, AuditActor.From(context.User), "consent.read", ConsentEvents.ResourceType, patientRef, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(consents.Select(ConsentResponse.From).ToArray());
    }
}
