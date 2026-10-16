using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Contracts.Patients;
using CareFlow.PatientVault.Api.Common;
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
    PatientKeyRing patientKeys,
    TimeProvider time,
    VaultMetrics metrics,
    ILogger<RegisterPatientHandler> logger)
{
    public async Task<Result<PatientRecord>> HandleAsync(RegisterPatientRequest request, string actor, CancellationToken cancellationToken)
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

        // Durante uma rotação, o mesmo CPF tem um índice por versão da chave da clínica; o índice único só pega
        // a versão atual, então as anteriores são conferidas aqui.
        var indexes = await keys.CpfIndexesAsync(cpf.Value, cancellationToken).ConfigureAwait(false);
        if (indexes.Count > 1 && await db.Patients.AnyAsync(p => p.CpfIndex != null && indexes.Contains(p.CpfIndex), cancellationToken).ConfigureAwait(false))
        {
            metrics.Duplicate();
            LogDuplicate(logger);
            return PatientErrors.DuplicateCpf;
        }

        // Cada paciente tem a própria chave (ADR-0012): é ela que a eliminação destrói.
        var patientKey = await patientKeys.CreateAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        var record = PatientProtector.Protect(data.Value, patientKey, key, now);
        db.Patients.Add(record);

        // Na mesma transação do cadastro: a agenda passa a conhecer o pseudônimo e a auditoria registra quem cadastrou.
        db.Enqueue(record.TenantId, new PatientRegistered(record.PatientRef, now), now);
        db.Record(record.TenantId, record.PatientRef, actor, VaultAudit.Registered, now);

        try
        {
            // Unicidade garantida pelo índice único (clínica, blind index), sem consulta prévia sujeita a corrida.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(PatientRecordConfiguration.CpfIndexConstraint))
        {
            // Descarta o cadastro e os eventos dele: nada foi gravado.
            db.ChangeTracker.Clear();
            metrics.Duplicate();
            LogDuplicate(logger);
            return PatientErrors.DuplicateCpf;
        }

        metrics.Registered();
        LogRegistered(logger, key.Version);
        return record;
    }

    // Nenhum log do cofre carrega dado pessoal ou patient_ref; a correlação vem do trace da requisição.
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Paciente cadastrado com chave própria, embrulhada pela chave de clínica versão {KeyVersion}")]
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
        var result = await handler.HandleAsync(request, AuditActor.From(response.HttpContext.User), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var record = result.Value;
        response.Headers.ETag = EntityTag.Format(record.RowVersion);
        return TypedResults.Created($"/v1/patients/{record.PatientRef}", new PatientRefResponse(record.PatientRef));
    }
}
