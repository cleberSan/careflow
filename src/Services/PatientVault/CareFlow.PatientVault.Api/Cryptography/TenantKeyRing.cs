using System.Security.Cryptography;

using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.PatientVault.Api.Common.Observability;
using CareFlow.PatientVault.Api.Cryptography.KeyManagement;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CareFlow.PatientVault.Api.Cryptography;

/// <summary>Quanto tempo uma DEK decifrada fica em memória antes de ser pedida de novo ao KMS.</summary>
internal sealed class KeyCacheOptions
{
    public const string SectionName = "KeyManagement:Cache";

    [System.ComponentModel.DataAnnotations.Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Chaves de dados (DEK) da clínica atual: cria na primeira gravação, decifra pelo KMS e mantém em cache
/// por pouco tempo. O cache evita uma chamada ao KMS por requisição (custo e latência em nuvem), ao preço
/// de manter a chave em memória pelo TTL configurado.
/// </summary>
internal sealed class TenantKeyRing(
    VaultDbContext db,
    ITenantContext tenant,
    IKeyManagementService kms,
    IMemoryCache cache,
    TimeProvider time,
    VaultMetrics metrics,
    IOptions<KeyCacheOptions> cacheOptions)
{
    public const int InitialVersion = 1;

    /// <summary>Chave atual da clínica, ou <c>null</c> se ela ainda não gravou nenhum paciente.</summary>
    public async Task<TenantDataKey?> FindCurrentAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (cache.TryGetValue(CurrentCacheKey(tenantId), out TenantDataKey? cached))
        {
            return cached;
        }

        var record = await db.TenantKeys
            .AsNoTracking()
            .OrderByDescending(k => k.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : await UnwrapAndCacheAsync(record, current: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Chave de uma versão específica, para decifrar registros gravados com ela.</summary>
    public async Task<TenantDataKey> GetAsync(int version, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (cache.TryGetValue(VersionCacheKey(tenantId, version), out TenantDataKey? cached) && cached is not null)
        {
            return cached;
        }

        var record = await db.TenantKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Version == version, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CryptographicException("A versão de chave do registro não existe para esta clínica.");

        return await UnwrapAndCacheAsync(record, current: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Chave atual da clínica, criando a primeira se ainda não existir.</summary>
    public async Task<TenantDataKey> GetOrCreateCurrentAsync(CancellationToken cancellationToken)
    {
        var current = await FindCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            return current;
        }

        var tenantId = tenant.TenantId;
        var dataKey = RandomNumberGenerator.GetBytes(LocalKeyManagementService.KeySizeBytes);
        try
        {
            var wrapped = await kms.WrapKeyAsync(dataKey, new KeyWrapContext(tenantId, InitialVersion), cancellationToken)
                .ConfigureAwait(false);
            var record = new TenantKeyRecord
            {
                TenantId = tenantId,
                Version = InitialVersion,
                KeyEncryptionKeyId = wrapped.KeyEncryptionKeyId,
                WrappedKey = wrapped.Ciphertext,
                CreatedAt = time.GetUtcNow(),
            };

            db.TenantKeys.Add(record);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation(TenantKeyRecordConfiguration.PrimaryKey))
            {
                // Outra requisição criou a chave da clínica ao mesmo tempo: usa a dela.
                db.Entry(record).State = EntityState.Detached;
                return await FindCurrentAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Chave da clínica criada em paralelo, mas não encontrada.", ex);
            }

            metrics.TenantKeyCreated();
            var key = TenantDataKey.Derive(tenantId, InitialVersion, dataKey);
            Cache(key, current: true);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Todas as versões da chave da clínica, da mais nova para a mais antiga.</summary>
    public async Task<IReadOnlyList<TenantDataKey>> AllAsync(CancellationToken cancellationToken)
    {
        var versions = await db.TenantKeys.AsNoTracking()
            .OrderByDescending(k => k.Version)
            .Select(k => k.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var keys = new List<TenantDataKey>(versions.Count);
        foreach (var version in versions)
        {
            keys.Add(await GetAsync(version, cancellationToken).ConfigureAwait(false));
        }

        return keys;
    }

    /// <summary>O blind index do CPF em cada versão da chave da clínica (normalmente uma só; duas durante uma rotação).</summary>
    public async Task<IReadOnlyList<byte[]>> CpfIndexesAsync(Domain.Patients.Cpf cpf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cpf);
        var keys = await AllAsync(cancellationToken).ConfigureAwait(false);
        return [.. keys.Select(key => PatientProtector.IndexCpf(cpf, key))];
    }

    /// <summary>
    /// Rotação: cria a versão seguinte da chave da clínica, que passa a ser a atual. As chaves dos pacientes e os
    /// blind indexes migram depois, em lotes (<c>KeyMaintenance</c>); as versões antigas continuam decifrando até lá.
    /// </summary>
    public async Task<TenantDataKey> RotateAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var latest = await db.TenantKeys.AsNoTracking()
            .OrderByDescending(k => k.Version)
            .Select(k => (int?)k.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var version = (latest ?? 0) + 1;

        var dataKey = RandomNumberGenerator.GetBytes(LocalKeyManagementService.KeySizeBytes);
        try
        {
            var wrapped = await kms.WrapKeyAsync(dataKey, new KeyWrapContext(tenantId, version), cancellationToken).ConfigureAwait(false);
            db.TenantKeys.Add(new TenantKeyRecord
            {
                TenantId = tenantId,
                Version = version,
                KeyEncryptionKeyId = wrapped.KeyEncryptionKeyId,
                WrappedKey = wrapped.Ciphertext,
                CreatedAt = time.GetUtcNow(),
            });

            // Rotação simultânea da mesma clínica: a chave primária recusa a segunda com a mesma versão.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            metrics.TenantKeyCreated();

            var key = TenantDataKey.Derive(tenantId, version, dataKey);
            Cache(key, current: true);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Tira uma versão do cache desta instância depois de destruída. Outras instâncias a esquecem no TTL.</summary>
    public void Forget(int version)
    {
        var tenantId = tenant.TenantId;
        cache.Remove(VersionCacheKey(tenantId, version));
        if (cache.TryGetValue(CurrentCacheKey(tenantId), out TenantDataKey? current) && current?.Version == version)
        {
            cache.Remove(CurrentCacheKey(tenantId));
        }
    }

    private async Task<TenantDataKey> UnwrapAndCacheAsync(TenantKeyRecord record, bool current, CancellationToken cancellationToken)
    {
        var dataKey = await kms.UnwrapKeyAsync(
                new WrappedKey(record.KeyEncryptionKeyId, record.WrappedKey),
                new KeyWrapContext(record.TenantId, record.Version),
                cancellationToken)
            .ConfigureAwait(false);
        metrics.KeyUnwrapped();

        var key = TenantDataKey.Derive(record.TenantId, record.Version, dataKey);
        Cache(key, current);
        return key;
    }

    private void Cache(TenantDataKey key, bool current)
    {
        var ttl = cacheOptions.Value.TimeToLive;
        cache.Set(VersionCacheKey(key.TenantId, key.Version), key, ttl);
        if (current)
        {
            cache.Set(CurrentCacheKey(key.TenantId), key, ttl);
        }
    }

    private static string CurrentCacheKey(Guid tenantId) => $"vault:dek:{tenantId:N}:current";

    private static string VersionCacheKey(Guid tenantId, int version) => $"vault:dek:{tenantId:N}:v{version}";
}
