using System.Security.Cryptography;
using System.Text;

namespace CareFlow.PatientVault.Api.Cryptography;

/// <summary>
/// Subchaves derivadas (HKDF-SHA256) da DEK de uma clínica: uma para cifrar campos e outra para o
/// blind index do CPF. Separar por finalidade evita reutilizar a mesma chave em dois algoritmos.
/// </summary>
internal sealed class TenantDataKey
{
    private static readonly byte[] EncryptionInfo = Encoding.UTF8.GetBytes("careflow:vault:field-encryption:v1");
    private static readonly byte[] IndexInfo = Encoding.UTF8.GetBytes("careflow:vault:blind-index:v1");

    private readonly byte[] _encryptionKey;
    private readonly byte[] _indexKey;

    private TenantDataKey(Guid tenantId, int version, byte[] encryptionKey, byte[] indexKey)
    {
        TenantId = tenantId;
        Version = version;
        _encryptionKey = encryptionKey;
        _indexKey = indexKey;
    }

    public Guid TenantId { get; }

    public int Version { get; }

    /// <summary>Deriva as subchaves e apaga a DEK recebida da memória.</summary>
    public static TenantDataKey Derive(Guid tenantId, int version, byte[] dataKey)
    {
        ArgumentNullException.ThrowIfNull(dataKey);
        try
        {
            return new TenantDataKey(
                tenantId,
                version,
                HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: [], info: EncryptionInfo),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: [], info: IndexInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public byte[] Encrypt(string plaintext, FieldContext context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return AeadEnvelope.Seal(_encryptionKey, Encoding.UTF8.GetBytes(plaintext), context.ToAssociatedData(TenantId));
    }

    public string Decrypt(byte[] envelope, FieldContext context)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Encoding.UTF8.GetString(AeadEnvelope.Open(_encryptionKey, envelope, context.ToAssociatedData(TenantId)));
    }

    /// <summary>
    /// HMAC-SHA256 determinístico com chave da clínica: permite busca exata e unicidade sem guardar o valor.
    /// O mesmo CPF gera índices diferentes em clínicas diferentes, então índices não cruzam clínicas.
    /// </summary>
    public byte[] BlindIndex(string normalizedValue)
    {
        ArgumentNullException.ThrowIfNull(normalizedValue);
        return HMACSHA256.HashData(_indexKey, Encoding.UTF8.GetBytes(normalizedValue));
    }
}

/// <summary>
/// Contexto autenticado (AAD) de um campo: clínica, paciente e nome do campo. Um valor cifrado copiado
/// para outro paciente, outra coluna ou outra clínica deixa de decifrar, o que detecta adulteração no banco.
/// </summary>
internal readonly record struct FieldContext(Guid PatientRef, string Field)
{
    public byte[] ToAssociatedData(Guid tenantId) =>
        Encoding.UTF8.GetBytes($"careflow:vault:field:v1|{tenantId:N}|{PatientRef:N}|{Field}");
}
