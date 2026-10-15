using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.PatientVault.Api.Common;
using CareFlow.PatientVault.Api.Common.Observability;
using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;
using CareFlow.PatientVault.Domain.Patients;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.PatientVault.Api.Features.Patients;

internal static class GetTriageProfileEndpoint
{
    /// <summary>
    /// O mínimo para a triagem (fase 7): a idade em anos completos, nunca a data de nascimento, e o nome, que o
    /// Triage usa só em memória para mascarar a queixa antes de ela ir ao modelo. CPF e contatos não saem. Cada
    /// leitura entra na trilha de auditoria com quem leu, antes da resposta.
    /// </summary>
    public static async Task<Results<Ok<TriageProfileResponse>, ProblemHttpResult>> HandleAsync(
        Guid patientRef,
        VaultDbContext db,
        TenantKeyRing keys,
        VaultMetrics metrics,
        TimeProvider time,
        HttpContext context,
        CancellationToken cancellationToken)
    {
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
        var now = time.GetUtcNow();

        db.Record(record.TenantId, record.PatientRef, AuditActor.From(context.User), VaultAudit.TriageProfileRead, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        metrics.Disclosed();
        return TypedResults.Ok(new TriageProfileResponse(AgeOn(patient.BirthDate, DateOnly.FromDateTime(now.UtcDateTime)), patient.FullName));
    }

    internal static int AgeOn(DateOnly birthDate, DateOnly today)
    {
        var age = today.Year - birthDate.Year;
        return today < birthDate.AddYears(age) ? Math.Max(0, age - 1) : Math.Max(0, age);
    }
}
