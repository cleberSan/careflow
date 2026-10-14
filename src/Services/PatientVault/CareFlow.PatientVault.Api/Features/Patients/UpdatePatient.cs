using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Results;
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
    TimeProvider time,
    VaultMetrics metrics,
    ILogger<UpdatePatientHandler> logger)
{
    /// <summary>Altera nome, nascimento e contatos. O CPF é imutável: identifica o paciente na clínica.</summary>
    public async Task<Result<PatientRecord>> HandleAsync(
        Guid patientRef,
        UpdatePatientRequest request,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        var record = await db.Patients
            .FirstOrDefaultAsync(p => p.PatientRef == patientRef, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return PatientErrors.NotFound;
        }

        if (record.RowVersion != expectedVersion)
        {
            return PatientErrors.StaleVersion;
        }

        var currentKey = await keys.GetAsync(record.KeyVersion, cancellationToken).ConfigureAwait(false);
        var cpf = Cpf.Create(PatientProtector.Unprotect(record, currentKey).Cpf).Value;

        var now = time.GetUtcNow();
        var data = PersonalData.Create(
            request.FullName, cpf, request.BirthDate, request.Email, request.Phone, DateOnly.FromDateTime(now.UtcDateTime));
        if (data.IsFailure)
        {
            return data.Error;
        }

        // Regrava com a chave atual da clínica: quando houver rotação de DEK, cada alteração já migra o registro.
        var key = await keys.GetOrCreateCurrentAsync(cancellationToken).ConfigureAwait(false);
        PatientProtector.Apply(record, data.Value, key, now);

        // A versão esperada vira a original do EF: se outra alteração gravar entre a leitura e o UPDATE,
        // o banco não encontra a linha com esse xmin e a gravação falha em vez de sobrescrever.
        db.Entry(record).Property(p => p.RowVersion).OriginalValue = expectedVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
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

        var result = await handler.HandleAsync(patientRef, request, version.Value, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        response.Headers.ETag = EntityTag.Format(result.Value.RowVersion);
        return TypedResults.NoContent();
    }
}
