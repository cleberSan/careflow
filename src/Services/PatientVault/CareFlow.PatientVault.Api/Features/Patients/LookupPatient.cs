using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.PatientVault.Api.Common;
using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;
using CareFlow.PatientVault.Domain.Patients;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.PatientVault.Api.Features.Patients;

internal static class LookupPatientEndpoint
{
    /// <summary>
    /// Busca exata pelo CPF via blind index. É POST para o CPF não aparecer em URL, logs de acesso ou proxies.
    /// Devolve só o pseudônimo.
    /// </summary>
    public static async Task<Results<Ok<PatientRefResponse>, ProblemHttpResult>> HandleAsync(
        LookupPatientRequest request,
        VaultDbContext db,
        TenantKeyRing keys,
        ITenantContext tenant,
        TimeProvider time,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var cpf = Cpf.Create(request.Cpf);
        if (cpf.IsFailure)
        {
            return cpf.Error.ToProblem();
        }

        var key = await keys.FindCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return PatientErrors.NotFound.ToProblem();
        }

        var index = PatientProtector.IndexCpf(cpf.Value, key);
        var patientRef = await db.Patients
            .AsNoTracking()
            .Where(p => p.CpfIndex == index)
            .Select(p => (Guid?)p.PatientRef)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (patientRef is null)
        {
            return PatientErrors.NotFound.ToProblem();
        }

        // Localizar alguém pelo CPF também é acesso ao dado: fica na trilha.
        db.Record(tenant.TenantId, patientRef.Value, AuditActor.From(httpContext.User), VaultAudit.LookedUp, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new PatientRefResponse(patientRef.Value));
    }
}
