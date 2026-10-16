using System.Security.Cryptography;
using System.Text;

namespace CareFlow.PatientVault.Api.Cryptography;

/// <summary>
/// DEK de um paciente (fase 8, ADR-0012): cifra os campos dele e só dele. Fica no banco embrulhada pela chave da
/// clínica (<see cref="TenantDataKey.WrapPatientKey"/>); destruir a linha embrulhada é eliminar o paciente
/// (crypto-shredding), porque nenhuma outra cópia dela existe. Nunca vai para cache: uma eliminação vale na hora em
/// todas as instâncias.
/// </summary>
internal sealed class PatientDataKey : IFieldCipher
{
    public const int KeySizeBytes = 32;

    private static readonly byte[] EncryptionInfo = Encoding.UTF8.GetBytes("careflow:vault:patient-field-encryption:v1");

    private readonly byte[] _encryptionKey;

    private PatientDataKey(Guid tenantId, Guid patientRef, byte[] encryptionKey)
    {
        TenantId = tenantId;
        PatientRef = patientRef;
        _encryptionKey = encryptionKey;
    }

    public Guid TenantId { get; }

    public Guid PatientRef { get; }

    /// <summary>Deriva a subchave de campos e apaga a DEK recebida da memória.</summary>
    public static PatientDataKey Derive(Guid tenantId, Guid patientRef, byte[] dataKey)
    {
        ArgumentNullException.ThrowIfNull(dataKey);
        try
        {
            if (dataKey.Length != KeySizeBytes)
            {
                throw new CryptographicException("A chave do paciente precisa ter 32 bytes.");
            }

            return new PatientDataKey(tenantId, patientRef, HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: [], info: EncryptionInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public byte[] Encrypt(string plaintext, FieldContext context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        EnsureSamePatient(context);
        return AeadEnvelope.Seal(_encryptionKey, Encoding.UTF8.GetBytes(plaintext), context.ToPatientKeyAssociatedData(TenantId));
    }

    public string Decrypt(byte[] envelope, FieldContext context)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        EnsureSamePatient(context);
        return Encoding.UTF8.GetString(AeadEnvelope.Open(_encryptionKey, envelope, context.ToPatientKeyAssociatedData(TenantId)));
    }

    // A chave de um paciente nunca cifra campo de outro: erro de programação, não dado inválido.
    private void EnsureSamePatient(FieldContext context)
    {
        if (context.PatientRef != PatientRef)
        {
            throw new InvalidOperationException("A chave do paciente só cifra campos do próprio paciente.");
        }
    }
}
