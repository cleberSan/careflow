using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Results;
using CareFlow.PatientVault.Api.Common.Http;
using CareFlow.PatientVault.Api.Common.Observability;
using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;
using CareFlow.PatientVault.Domain.Patients;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.PatientVault.Api.Features.Patients;

internal sealed partial class RegisterPatientHandler(
    VaultDbContext db,
    TenantKeyRing keys,
    TimeProvider time,
    VaultMetrics metrics,
    ILogger<RegisterPatientHandler> logger)
{
    public async Task<Result<PatientRecord>> HandleAsync(RegisterPatientRequest request, CancellationToken cancellationToken)
    {
        var cpf = Cpf.Create(request.Cpf);
        if (cpf.IsFailure)
        {
            return cpf.Error;
        }

        var now = time.GetUtcNow();
        var data = PersonalData.Create(
            request.FullName, cpf.Value, request.BirthDate, request.Email, request.Phone, DateOnly.FromDateTime(now.UtcDateTime));
        if (data.IsFailure)
        {
            return data.Error;
        }

        var key = await keys.GetOrCreateCurrentAsync(cancellationToken).ConfigureAwait(false);
        var record = PatientProtector.Protect(Guid.NewGuid(), data.Value, key, now);
        db.Patients.Add(record);

        try
        {
            // Unicidade garantida pelo índice único (clínica, blind index), sem consulta prévia sujeita a corrida.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(PatientRecordConfiguration.CpfIndexConstraint))
        {
            db.Entry(record).State = EntityState.Detached;
            metrics.Duplicate();
            LogDuplicate(logger);
            return PatientErrors.DuplicateCpf;
        }

        metrics.Registered();
        LogRegistered(logger, key.Version);
        return record;
    }

    // Nenhum log do cofre carrega dado pessoal ou patient_ref; a correlação vem do trace da requisição.
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Paciente cadastrado com a chave de clínica versão {KeyVersion}")]
    private static partial void LogRegistered(ILogger logger, int keyVersion);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Cadastro recusado: CPF já existente na clínica")]
    private static partial void LogDuplicate(ILogger logger);
}

internal static class RegisterPatientEndpoint
{
    public static async Task<Results<Created<PatientRefResponse>, ProblemHttpResult>> HandleAsync(
        RegisterPatientRequest request,
        RegisterPatientHandler handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var record = result.Value;
        response.Headers.ETag = EntityTag.Format(record.RowVersion);
        return TypedResults.Created($"/v1/patients/{record.PatientRef}", new PatientRefResponse(record.PatientRef));
    }
}
