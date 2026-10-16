using System.Security.Cryptography;
using System.Text;

using CareFlow.BuildingBlocks.Cryptography;

using Microsoft.Extensions.Options;

namespace CareFlow.Rights.Api.Common.Cryptography;

/// <summary>
/// Criptografia do serviço de pedidos (ADR-0012). Envelopes AES-256-GCM com o contexto (pedido e uso) como dado
/// autenticado: um envelope copiado para outro pedido não abre. O resumo do código é um HMAC com chave derivada,
/// porque 10⁶ códigos possíveis se quebram por força bruta a partir de um SHA-256 simples.
/// </summary>
internal sealed class RightsKeyRing : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _kek;
    private readonly byte[] _codeKey;

    public RightsKeyRing(IOptions<RightsKeyOptions> keys, IOptions<IdentityVerificationOptions> identity)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(identity);

        try
        {
            _kek = Convert.FromBase64String(keys.Value.KeyEncryptionKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{RightsKeyOptions.SectionName}:KeyEncryptionKey precisa ser Base64.", ex);
        }

        if (_kek.Length != KeySize)
        {
            throw new InvalidOperationException($"{RightsKeyOptions.SectionName}:KeyEncryptionKey precisa ter 32 bytes.");
        }

        _codeKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, _kek, KeySize, info: Encoding.UTF8.GetBytes("careflow:rights:code-hmac:v1"));

        // Chave pública inválida derruba a subida, não o primeiro pedido.
        NotificationsPublicKey = identity.Value.NotificationsPublicKey;
        using var check = ECDiffieHellman.Create();
        try
        {
            check.ImportSubjectPublicKeyInfo(Convert.FromBase64String(NotificationsPublicKey), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException($"{IdentityVerificationOptions.SectionName}:NotificationsPublicKey não é uma chave pública válida.", ex);
        }
    }

    public string NotificationsPublicKey { get; }

    /// <summary>Código de 6 dígitos, uniforme.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public byte[] CodeHash(Guid requestId, string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return HMACSHA256.HashData(_codeKey, Encoding.UTF8.GetBytes($"{requestId:N}|{code}"));
    }

    /// <summary>O código cifrado para o Notifications, amarrado ao pedido.</summary>
    public string SealCode(Guid requestId, string code) =>
        SealedBox.Seal(NotificationsPublicKey, Encoding.UTF8.GetBytes(code), Contracts.Rights.SubjectRequestSealing.VerificationCodeContext(requestId));

    public byte[] Wrap(ReadOnlySpan<byte> secret, string context)
    {
        var output = new byte[NonceSize + secret.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_kek, TagSize);
        aes.Encrypt(nonce, secret, output.AsSpan(NonceSize, secret.Length), output.AsSpan(NonceSize + secret.Length, TagSize), Encoding.UTF8.GetBytes(context));
        return output;
    }

    /// <summary>Lança <see cref="CryptographicException"/> se o envelope for de outro contexto ou estiver alterado.</summary>
    public byte[] Unwrap(byte[] wrapped, string context)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        if (wrapped.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Envelope truncado.");
        }

        var length = wrapped.Length - NonceSize - TagSize;
        var plaintext = new byte[length];
        using var aes = new AesGcm(_kek, TagSize);
        aes.Decrypt(
            wrapped.AsSpan(0, NonceSize),
            wrapped.AsSpan(NonceSize, length),
            wrapped.AsSpan(NonceSize + length, TagSize),
            plaintext,
            Encoding.UTF8.GetBytes(context));
        return plaintext;
    }

    public static string RecipientKeyContext(Guid requestId) => $"careflow:rights:recipient-key:v1|{requestId:N}";

    public static string PackageKeyContext(Guid requestId) => $"careflow:rights:package-key:v1|{requestId:N}";

    public static string PackageContentContext(Guid requestId) => $"careflow:rights:package:v1|{requestId:N}";

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_kek);
        CryptographicOperations.ZeroMemory(_codeKey);
    }
}
