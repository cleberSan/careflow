using System.Security.Cryptography;
using System.Text;

namespace CareFlow.PatientVault.Api.Cryptography;

/// <summary>Cifra campos de um paciente: a chave da clínica (esquema antigo) ou a do próprio paciente.</summary>
internal interface IFieldCipher
{
    byte[] Encrypt(string plaintext, FieldContext context);

    string Decrypt(byte[] envelope, FieldContext context);
}

/// <summary>
/// Subchaves derivadas (HKDF-SHA256) da DEK de uma clínica: uma para cifrar campos (registros do esquema antigo,
/// antes da chave por paciente), uma para o blind index do CPF e uma para embrulhar as chaves dos pacientes.
/// Separar por finalidade evita reutilizar a mesma chave em dois algoritmos.
/// </summary>
internal sealed class TenantDataKey : IFieldCipher
{
    private static readonly byte[] EncryptionInfo = Encoding.UTF8.GetBytes("careflow:vault:field-encryption:v1");
    private static readonly byte[] IndexInfo = Encoding.UTF8.GetBytes("careflow:vault:blind-index:v1");
    private static readonly byte[] WrappingInfo = Encoding.UTF8.GetBytes("careflow:vault:patient-key-wrap:v1");

    private readonly byte[] _encryptionKey;
    private readonly byte[] _indexKey;
    private readonly byte[] _wrappingKey;

    private TenantDataKey(Guid tenantId, int version, byte[] encryptionKey, byte[] indexKey, byte[] wrappingKey)
    {
        TenantId = tenantId;
        Version = version;
        _encryptionKey = encryptionKey;
        _indexKey = indexKey;
        _wrappingKey = wrappingKey;
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
                HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: [], info: IndexInfo),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: [], info: WrappingInfo));
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
    /// Embrulha a DEK de um paciente. O dado autenticado amarra a chave embrulhada à clínica, ao paciente e à versão
    /// da chave da clínica: copiada para outro paciente, ela não abre.
    /// </summary>
    public byte[] WrapPatientKey(Guid patientRef, ReadOnlySpan<byte> patientKey) =>
        AeadEnvelope.Seal(_wrappingKey, patientKey, PatientKeyContext(patientRef));

    /// <summary>Lança <see cref="CryptographicException"/> se a chave embrulhada não for deste paciente e desta versão.</summary>
    public byte[] UnwrapPatientKey(Guid patientRef, byte[] wrapped)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        return AeadEnvelope.Open(_wrappingKey, wrapped, PatientKeyContext(patientRef));
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

    private byte[] PatientKeyContext(Guid patientRef) =>
        Encoding.UTF8.GetBytes($"careflow:vault:patient-dek:v1|{TenantId:N}|{patientRef:N}|{Version}");
}

/// <summary>
/// Contexto autenticado (AAD) de um campo: clínica, paciente e nome do campo. Um valor cifrado copiado
/// para outro paciente, outra coluna ou outra clínica deixa de decifrar, o que detecta adulteração no banco.
/// </summary>
internal readonly record struct FieldContext(Guid PatientRef, string Field)
{
    public byte[] ToAssociatedData(Guid tenantId) =>
        Encoding.UTF8.GetBytes($"careflow:vault:field:v1|{tenantId:N}|{PatientRef:N}|{Field}");

    /// <summary>Contexto dos campos cifrados com a chave do paciente (esquema 2): versão própria, sem ambiguidade.</summary>
    public byte[] ToPatientKeyAssociatedData(Guid tenantId) =>
        Encoding.UTF8.GetBytes($"careflow:vault:field:v2|{tenantId:N}|{PatientRef:N}|{Field}");
}
