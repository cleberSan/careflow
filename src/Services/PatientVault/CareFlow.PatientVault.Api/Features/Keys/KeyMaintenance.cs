using System.Security.Cryptography;

using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Results;
using CareFlow.PatientVault.Api.Common;
using CareFlow.PatientVault.Api.Common.Observability;
using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.PatientVault.Api.Features.Keys;

/// <summary>Resultado de um lote de manutenção de chaves.</summary>
public sealed record KeyMaintenanceResult(int CurrentVersion, int Migrated, int Rewrapped, int Skipped, int Remaining);

/// <summary>
/// Leva os registros da clínica para a chave atual, um lote por chamada, sem parar o cofre (ADR-0012):
/// <list type="bullet">
/// <item>registro do esquema antigo (campos com a chave da clínica) ganha chave própria e é recifrado;</item>
/// <item>chave de paciente embrulhada por uma versão anterior da chave da clínica é reembrulhada;</item>
/// <item>o blind index do CPF é recalculado com a versão atual.</item>
/// </list>
/// Leituras continuam funcionando durante todo o processo, porque o cofre lê os dois esquemas e todas as versões.
/// Cada registro é gravado com concorrência otimista: se alguém alterou o paciente no meio, ele fica para o próximo
/// lote (e a própria alteração já gravou com a chave nova).
/// </summary>
internal sealed partial class KeyMaintenance(
    VaultDbContext db,
    TenantKeyRing tenantKeys,
    PatientKeyRing patientKeys,
    VaultMetrics metrics,
    TimeProvider time,
    ILogger<KeyMaintenance> logger)
{
    public const int MaxBatchSize = 500;

    public static readonly Error KeyInUse = Error.Conflict(
        "Chave.EmUso", "Esta versão da chave da clínica ainda protege registros. Rode a manutenção até não restar nenhum.");

    public static readonly Error CurrentKey = Error.Conflict(
        "Chave.Atual", "A versão atual da chave da clínica não pode ser destruída. Rotacione antes.");

    public static readonly Error KeyNotFound = Error.NotFound("Chave.NaoEncontrada", "Versão de chave não encontrada nesta clínica.");

    public async Task<KeyMaintenanceResult> RunBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        batchSize = Math.Clamp(batchSize, 1, MaxBatchSize);
        var current = await tenantKeys.FindCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new KeyMaintenanceResult(0, 0, 0, 0, 0);
        }

        var candidates = await Pending(current.Version)
            .OrderBy(p => p.PatientRef)
            .Select(p => p.PatientRef)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int migrated = 0, rewrapped = 0, skipped = 0;
        foreach (var patientRef in candidates)
        {
            switch (await UpgradeAsync(patientRef, current, cancellationToken).ConfigureAwait(false))
            {
                case Upgrade.Migrated:
                    migrated++;
                    break;
                case Upgrade.Rewrapped:
                    rewrapped++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        var remaining = await Pending(current.Version).CountAsync(cancellationToken).ConfigureAwait(false);
        LogBatch(logger, current.Version, migrated, rewrapped, skipped, remaining);
        return new KeyMaintenanceResult(current.Version, migrated, rewrapped, skipped, remaining);
    }

    /// <summary>
    /// Destrói uma versão antiga da chave da clínica, depois que nenhum registro depende mais dela. Destruir a versão
    /// que existia na data de um backup torna ilegível tudo o que aquele backup tinha cifrado com ela.
    /// </summary>
    public async Task<Result> DestroyAsync(int version, string actor, CancellationToken cancellationToken)
    {
        var record = await db.TenantKeys.FirstOrDefaultAsync(k => k.Version == version, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Result.Failure(KeyNotFound);
        }

        var latest = await db.TenantKeys.MaxAsync(k => k.Version, cancellationToken).ConfigureAwait(false);
        if (version == latest)
        {
            return Result.Failure(CurrentKey);
        }

        var inUse = await db.Patients.AnyAsync(p => p.KeyVersion == version, cancellationToken).ConfigureAwait(false)
            || await db.PatientKeys.AnyAsync(k => k.TenantKeyVersion == version, cancellationToken).ConfigureAwait(false);
        if (inUse)
        {
            return Result.Failure(KeyInUse);
        }

        db.TenantKeys.Remove(record);
        db.Record(record.TenantId, record.TenantId, actor, VaultAudit.TenantKeyDestroyed, VaultAudit.TenantKeyResource, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.IsForeignKeyViolation())
        {
            // Um registro passou a usar a versão entre a conferência e o DELETE: a chave estrangeira segura.
            db.ChangeTracker.Clear();
            return Result.Failure(KeyInUse);
        }

        tenantKeys.Forget(version);
        metrics.TenantKeyDestroyed();
        LogDestroyed(logger, version);
        return Result.Success();
    }

    private IQueryable<PatientRecord> Pending(int currentVersion) =>
        db.Patients.Where(p =>
            p.KeyVersion != currentVersion
            || (p.ErasedAt == null && p.KeyScheme == KeyScheme.ClinicKey)
            || db.PatientKeys.Any(k => k.PatientRef == p.PatientRef && k.TenantKeyVersion != currentVersion));

    private async Task<Upgrade> UpgradeAsync(Guid patientRef, TenantDataKey current, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var record = await db.Patients.FirstOrDefaultAsync(p => p.PatientRef == patientRef, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Upgrade.Skipped;
        }

        var now = time.GetUtcNow();
        Upgrade outcome;
        if (record.IsErased)
        {
            // Nada a decifrar: só deixa de apontar para a versão antiga, que assim pode ser destruída.
            record.KeyVersion = current.Version;
            outcome = Upgrade.Rewrapped;
        }
        else
        {
            var data = PatientProtector.Unprotect(record, await patientKeys.ForReadingAsync(record, cancellationToken).ConfigureAwait(false))
                .ToPersonalData(DateOnly.FromDateTime(now.UtcDateTime));
            if (record.KeyScheme == KeyScheme.ClinicKey)
            {
                var patientKey = await patientKeys.ForWritingAsync(record, cancellationToken).ConfigureAwait(false);
                PatientProtector.Apply(record, data, patientKey, current, now);
                outcome = Upgrade.Migrated;
            }
            else
            {
                await RewrapAsync(record.PatientRef, current, now, cancellationToken).ConfigureAwait(false);
                record.KeyVersion = current.Version;
                record.CpfIndex = PatientProtector.IndexCpf(data.Cpf, current);
                record.UpdatedAt = now;
                outcome = Upgrade.Rewrapped;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation(PatientKeyRecordConfiguration.PrimaryKey))
        {
            // Alteração ou eliminação concorrente: o registro volta no próximo lote, se ainda precisar.
            db.ChangeTracker.Clear();
            LogSkipped(logger);
            return Upgrade.Skipped;
        }
    }

    private async Task RewrapAsync(Guid patientRef, TenantDataKey current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.PatientKeys.FirstOrDefaultAsync(k => k.PatientRef == patientRef, cancellationToken).ConfigureAwait(false)
            ?? throw new CryptographicException("A chave do paciente não existe (registro inconsistente).");
        if (row.TenantKeyVersion == current.Version)
        {
            return;
        }

        var previous = await tenantKeys.GetAsync(row.TenantKeyVersion, cancellationToken).ConfigureAwait(false);
        var dataKey = previous.UnwrapPatientKey(row.PatientRef, row.WrappedKey);
        try
        {
            row.WrappedKey = current.WrapPatientKey(row.PatientRef, dataKey);
            row.TenantKeyVersion = current.Version;
            row.UpdatedAt = now;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private enum Upgrade
    {
        Skipped,
        Migrated,
        Rewrapped,
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Manutenção de chaves (versão atual {Version}): {Migrated} migrados, {Rewrapped} reembrulhados, {Skipped} adiados, {Remaining} restantes")]
    private static partial void LogBatch(ILogger logger, int version, int migrated, int rewrapped, int skipped, int remaining);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "Registro alterado durante a manutenção de chaves; fica para o próximo lote")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Versão {Version} da chave da clínica destruída")]
    private static partial void LogDestroyed(ILogger logger, int version);
}
