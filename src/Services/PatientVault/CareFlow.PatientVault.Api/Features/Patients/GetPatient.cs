using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.PatientVault.Api.Common.Http;
using CareFlow.PatientVault.Api.Common.Observability;
using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;
using CareFlow.PatientVault.Domain.Patients;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.PatientVault.Api.Features.Patients;

internal static class GetPatientEndpoint
{
    public static async Task<Results<Ok<PatientResponse>, ProblemHttpResult>> HandleAsync(
        Guid patientRef,
        VaultDbContext db,
        TenantKeyRing keys,
        VaultMetrics metrics,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        // Filtro do EF e RLS no banco: paciente de outra clínica é 404, sem revelar que existe (IDOR).
        var record = await db.Patients
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PatientRef == patientRef, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return PatientErrors.NotFound.ToProblem();
        }

        var key = await keys.GetAsync(record.KeyVersion, cancellationToken).ConfigureAwait(false);
        var patient = PatientProtector.Unprotect(record, key);

        metrics.Disclosed();
        response.Headers.ETag = EntityTag.Format(record.RowVersion);
        return TypedResults.Ok(PatientResponse.From(record.PatientRef, patient, record.CreatedAt, record.UpdatedAt));
    }
}
