using System.Security.Cryptography;

using CareFlow.PatientVault.Api.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.PatientVault.Api.Cryptography;

/// <summary>
/// Chaves dos pacientes da clínica atual (ADR-0012). Cria a chave no cadastro (ou na primeira gravação de um
/// registro antigo), abre a chave para ler e destrói na eliminação. Chaves de paciente não vão para cache.
/// </summary>
internal sealed class PatientKeyRing(VaultDbContext db, TenantKeyRing tenantKeys, TimeProvider time)
{
    /// <summary>Cria a chave de um paciente novo, embrulhada pela chave atual da clínica. Grava no próximo SaveChanges.</summary>
    public async Task<PatientDataKey> CreateAsync(Guid patientRef, CancellationToken cancellationToken)
    {
        var tenantKey = await tenantKeys.GetOrCreateCurrentAsync(cancellationToken).ConfigureAwait(false);
        var dataKey = RandomNumberGenerator.GetBytes(PatientDataKey.KeySizeBytes);
        try
        {
            var now = time.GetUtcNow();
            db.PatientKeys.Add(new PatientKeyRecord
            {
                TenantId = tenantKey.TenantId,
                PatientRef = patientRef,
                TenantKeyVersion = tenantKey.Version,
                WrappedKey = tenantKey.WrapPatientKey(patientRef, dataKey),
                CreatedAt = now,
                UpdatedAt = now,
            });

            return PatientDataKey.Derive(tenantKey.TenantId, patientRef, dataKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    /// A chave que decifra os campos do registro: a da clínica (esquema antigo) ou a do paciente. Registro
    /// eliminado não tem chave; quem chama confere <see cref="PatientRecord.IsErased"/> antes.
    /// </summary>
    public async Task<IFieldCipher> ForReadingAsync(PatientRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.KeyScheme == KeyScheme.ClinicKey)
        {
            return await tenantKeys.GetAsync(record.KeyVersion, cancellationToken).ConfigureAwait(false);
        }

        return await FindAsync(record.PatientRef, cancellationToken).ConfigureAwait(false)
            ?? throw new CryptographicException("A chave do paciente não existe (eliminado ou registro inconsistente).");
    }

    /// <summary>A chave do paciente para gravar; cria uma se o registro ainda é do esquema antigo.</summary>
    public async Task<PatientDataKey> ForWritingAsync(PatientRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return await FindAsync(record.PatientRef, cancellationToken).ConfigureAwait(false)
            ?? await CreateAsync(record.PatientRef, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PatientDataKey?> FindAsync(Guid patientRef, CancellationToken cancellationToken)
    {
        var row = await db.PatientKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.PatientRef == patientRef, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var tenantKey = await tenantKeys.GetAsync(row.TenantKeyVersion, cancellationToken).ConfigureAwait(false);
        return PatientDataKey.Derive(row.TenantId, row.PatientRef, tenantKey.UnwrapPatientKey(row.PatientRef, row.WrappedKey));
    }

    /// <summary>
    /// Destrói a chave do paciente (apaga a linha, que é a única cópia). Devolve quantas linhas saíram: zero numa
    /// eliminação repetida.
    /// </summary>
    public Task<int> DestroyAsync(Guid patientRef, CancellationToken cancellationToken) =>
        db.PatientKeys.Where(k => k.PatientRef == patientRef).ExecuteDeleteAsync(cancellationToken);
}
