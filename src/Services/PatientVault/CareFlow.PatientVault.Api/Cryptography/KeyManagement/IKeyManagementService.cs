using System.Text;

namespace CareFlow.PatientVault.Api.Cryptography.KeyManagement;

/// <summary>
/// Serviço de gestão de chaves (KMS) que guarda a chave mestra (KEK) e cifra/decifra as chaves de dados
/// das clínicas (DEK). A KEK nunca sai do provedor; o cofre só vê DEKs cifradas no banco e, em memória,
/// as DEKs decifradas que estão em uso. Implementações: local (desenvolvimento e testes) e, quando a nuvem
/// for escolhida, Azure Key Vault ou AWS KMS.
/// </summary>
public interface IKeyManagementService
{
    /// <summary>Cifra uma DEK com a KEK ativa, amarrada ao contexto (clínica e versão).</summary>
    Task<WrappedKey> WrapKeyAsync(ReadOnlyMemory<byte> dataKey, KeyWrapContext context, CancellationToken cancellationToken);

    /// <summary>Decifra uma DEK. Falha se o contexto não for o mesmo usado na cifragem.</summary>
    Task<byte[]> UnwrapKeyAsync(WrappedKey wrappedKey, KeyWrapContext context, CancellationToken cancellationToken);
}

/// <summary>DEK cifrada e o identificador da KEK que a cifrou (permite rotacionar a KEK sem perder DEKs antigas).</summary>
public sealed record WrappedKey(string KeyEncryptionKeyId, byte[] Ciphertext);

/// <summary>
/// Contexto autenticado da cifragem da DEK (AAD no AES-GCM, EncryptionContext no AWS KMS). Impede que a DEK
/// cifrada de uma clínica seja copiada para outra linha e decifrada como se fosse de outra clínica.
/// </summary>
public readonly record struct KeyWrapContext(Guid TenantId, int KeyVersion)
{
    public byte[] ToAssociatedData() =>
        Encoding.UTF8.GetBytes($"careflow:vault:tenant-dek:v1|{TenantId:N}|{KeyVersion}");
}
