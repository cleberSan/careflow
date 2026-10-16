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

internal static class GetPatientContactEndpoint
{
    /// <summary>
    /// Só telefone e e-mail, para o Notifications avisar o paciente na hora do envio (minimização: nome, CPF e
    /// nascimento não saem). Cada leitura entra na trilha de auditoria com quem leu, antes da resposta.
    /// </summary>
    public static async Task<Results<Ok<PatientContactResponse>, ProblemHttpResult>> HandleAsync(
        Guid patientRef,
        VaultDbContext db,
        PatientKeyRing keys,
        VaultMetrics metrics,
        TimeProvider time,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var record = await db.Patients
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PatientRef == patientRef, cancellationToken)
            .ConfigureAwait(false);
        // Eliminado é 404 aqui: para quem envia avisos, o paciente simplesmente não tem mais contato.
        if (record is null || record.IsErased)
        {
            return PatientErrors.NotFound.ToProblem();
        }

        var key = await keys.ForReadingAsync(record, cancellationToken).ConfigureAwait(false);
        var patient = PatientProtector.Unprotect(record, key);

        db.Record(record.TenantId, record.PatientRef, AuditActor.From(context.User), VaultAudit.ContactRead, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        metrics.Disclosed();
        return TypedResults.Ok(new PatientContactResponse(patient.Email, patient.Phone));
    }
}
