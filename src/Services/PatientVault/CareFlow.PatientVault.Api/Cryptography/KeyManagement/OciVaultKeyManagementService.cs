using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace CareFlow.PatientVault.Api.Cryptography.KeyManagement;

/// <summary>
/// Chave mestra no OCI Vault (ADR-0013): uma chave AES protegida por software, que nunca sai do Vault. A VM se
/// autentica por instance principal, então nenhuma credencial da Oracle fica em disco, em variável ou no repositório.
/// </summary>
public sealed class OciVaultKeyManagementOptions
{
    public const string SectionName = "KeyManagement:OciVault";

    /// <summary>Endpoint de criptografia do vault (página do vault no console, "Cryptographic Endpoint").</summary>
    [Required]
    public Uri? CryptoEndpoint { get; set; }

    /// <summary>OCID da chave mestra que cifra as DEKs novas. Trocar o OCID rotaciona a KEK; as antigas continuam decifrando.</summary>
    [Required]
    [RegularExpression(@"^ocid1\.key\.[a-z0-9._-]+$", ErrorMessage = "KeyManagement:OciVault:KeyId precisa ser o OCID de uma chave (ocid1.key...).")]
    public string KeyId { get; set; } = string.Empty;
}

/// <summary>As duas operações do OCI Vault que o cofre usa, isoladas para poder testar sem a Oracle.</summary>
public interface IOciCrypto
{
    /// <summary>Cifra com a versão atual da chave; devolve o ciphertext em Base64, como o Vault entrega.</summary>
    Task<string> EncryptAsync(string keyId, string plaintextBase64, IReadOnlyDictionary<string, string> associatedData, CancellationToken cancellationToken);

    /// <summary>Decifra; o Vault recusa se o dado associado não for o mesmo da cifragem.</summary>
    Task<string> DecryptAsync(string keyId, string ciphertextBase64, IReadOnlyDictionary<string, string> associatedData, CancellationToken cancellationToken);
}

/// <summary>
/// KMS do OCI Vault. A DEK da clínica é cifrada no Vault com o contexto (clínica e versão) como dado associado; o
/// ciphertext do Vault já carrega a versão da chave, então rotacionar a chave no console não quebra DEKs antigas.
/// O identificador guardado em <c>tenant_keys</c> é <c>oci:</c> + OCID, para nunca confundir com uma chave local.
/// </summary>
public sealed class OciVaultKeyManagementService(IOciCrypto crypto, Microsoft.Extensions.Options.IOptions<OciVaultKeyManagementOptions> options)
    : IKeyManagementService
{
    public const string KeyIdPrefix = "oci:";

    private const string ContextEntry = "careflow-context";

    private readonly string _activeKeyId = options.Value.KeyId;

    public async Task<WrappedKey> WrapKeyAsync(ReadOnlyMemory<byte> dataKey, KeyWrapContext context, CancellationToken cancellationToken)
    {
        var ciphertext = await crypto.EncryptAsync(_activeKeyId, Convert.ToBase64String(dataKey.Span), AssociatedData(context), cancellationToken)
            .ConfigureAwait(false);
        return new WrappedKey(KeyIdPrefix + _activeKeyId, Decode(ciphertext));
    }

    public async Task<byte[]> UnwrapKeyAsync(WrappedKey wrappedKey, KeyWrapContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wrappedKey);
        if (!wrappedKey.KeyEncryptionKeyId.StartsWith(KeyIdPrefix, StringComparison.Ordinal))
        {
            // DEK cifrada pela chave local (desenvolvimento): o OCI Vault não tem como abrir.
            throw new CryptographicException("A chave de dados foi cifrada por outro provedor de chaves.");
        }

        var keyId = wrappedKey.KeyEncryptionKeyId[KeyIdPrefix.Length..];
        var plaintext = await crypto.DecryptAsync(keyId, Convert.ToBase64String(wrappedKey.Ciphertext), AssociatedData(context), cancellationToken)
            .ConfigureAwait(false);
        return Decode(plaintext);
    }

    /// <summary>O mesmo dado autenticado do provedor local, numa entrada só: uma regra de contexto para os dois.</summary>
    private static Dictionary<string, string> AssociatedData(KeyWrapContext context) =>
        new(StringComparer.Ordinal) { [ContextEntry] = Encoding.UTF8.GetString(context.ToAssociatedData()) };

    private static byte[] Decode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("O OCI Vault devolveu um valor fora do formato esperado.", exception);
        }
    }
}
