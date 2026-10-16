using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Results;
using CareFlow.PatientVault.Api.Common;
using CareFlow.PatientVault.Api.Common.Http;
using CareFlow.PatientVault.Api.Common.Observability;
using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;
using CareFlow.PatientVault.Domain.Patients;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.PatientVault.Api.Features.Patients;

internal sealed partial class UpdatePatientHandler(
    VaultDbContext db,
    TenantKeyRing keys,
    PatientKeyRing patientKeys,
    TimeProvider time,
    VaultMetrics metrics,
    ILogger<UpdatePatientHandler> logger)
{
    /// <summary>Altera nome, nascimento e contatos. O CPF é imutável: identifica o paciente na clínica.</summary>
    public async Task<Result<PatientRecord>> HandleAsync(
        Guid patientRef,
        UpdatePatientRequest request,
        uint expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var record = await db.Patients
            .FirstOrDefaultAsync(p => p.PatientRef == patientRef, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return PatientErrors.NotFound;
        }

        if (record.IsErased)
        {
            return PatientErrors.Erased;
        }

        if (record.RowVersion != expectedVersion)
        {
            return PatientErrors.StaleVersion;
        }

        var readKey = await patientKeys.ForReadingAsync(record, cancellationToken).ConfigureAwait(false);
        var cpf = Cpf.Create(PatientProtector.Unprotect(record, readKey).Cpf).Value;

        var now = time.GetUtcNow();
        var data = PersonalData.Create(
            request.FullName, cpf, request.BirthDate, request.Email, request.Phone, DateOnly.FromDateTime(now.UtcDateTime));
        if (data.IsFailure)
        {
            return data.Error;
        }

        // Regrava com a chave do paciente (criada aqui se o registro ainda era do esquema antigo) e o índice com a
        // chave atual da clínica: cada alteração já migra o registro.
        var indexKey = await keys.GetOrCreateCurrentAsync(cancellationToken).ConfigureAwait(false);
        var patientKey = await patientKeys.ForWritingAsync(record, cancellationToken).ConfigureAwait(false);
        PatientProtector.Apply(record, data.Value, patientKey, indexKey, now);
        db.Record(record.TenantId, record.PatientRef, actor, VaultAudit.Updated, now);

        // A versão esperada vira a original do EF: se outra alteração gravar entre a leitura e o UPDATE,
        // o banco não encontra a linha com esse xmin e a gravação falha em vez de sobrescrever.
        db.Entry(record).Property(p => p.RowVersion).OriginalValue = expectedVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            LogConcurrentUpdate(logger);
            return PatientErrors.StaleVersion;
        }

        metrics.Updated();
        return record;
    }

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Alteração de paciente recusada: versão desatualizada")]
    private static partial void LogConcurrentUpdate(ILogger logger);
}

internal static class UpdatePatientEndpoint
{
    /// <summary>Responde 204 sem corpo: quem só tem escrita não recebe de volta dados que não enviou (como o CPF).</summary>
    public static async Task<Results<NoContent, ProblemHttpResult>> HandleAsync(
        Guid patientRef,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        UpdatePatientRequest request,
        UpdatePatientHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var version = EntityTag.ParseIfMatch(ifMatch);
        if (version.IsFailure)
        {
            return version.Error.ToProblem();
        }

        var result = await handler.HandleAsync(patientRef, request, version.Value, AuditActor.From(response.HttpContext.User), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        response.Headers.ETag = EntityTag.Format(result.Value.RowVersion);
        return TypedResults.NoContent();
    }
}
