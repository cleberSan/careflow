using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.Contracts.Rights;

namespace CareFlow.Notifications.Api.Features.Rights;

internal sealed class IdentityVerificationOptions
{
    public const string SectionName = "IdentityVerification";

    /// <summary>
    /// Chave privada P-256 (PKCS#8 em Base64) que abre o código de confirmação vindo do serviço de direitos, que tem a
    /// pública. Só esta chave abre: Outbox, broker e Inbox guardam o código cifrado. Em produção, vem de um cofre.
    /// </summary>
    [Required]
    public string? PrivateKey { get; set; }
}

/// <summary>Abre o código de confirmação de identidade (ADR-0012). Singleton: a chave fica carregada uma vez.</summary>
internal sealed class VerificationCodeKey : IDisposable
{
    public const int CodeLength = 6;

    private readonly ECDiffieHellman _key;

    public VerificationCodeKey(Microsoft.Extensions.Options.IOptions<IdentityVerificationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _key = SealedBox.ImportPrivateKey(options.Value.PrivateKey!);
    }

    /// <summary>O código em claro, ou nulo se o envelope não abrir ou não for um código de 6 dígitos.</summary>
    public string? TryOpen(Guid requestId, string sealedCode)
    {
        byte[] plaintext;
        try
        {
            plaintext = SealedBox.Open(_key, sealedCode, SubjectRequestSealing.VerificationCodeContext(requestId));
        }
        catch (CryptographicException)
        {
            return null;
        }

        try
        {
            var code = Encoding.ASCII.GetString(plaintext);
            return code.Length == CodeLength && code.All(char.IsAsciiDigit) ? code : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose() => _key.Dispose();
}
