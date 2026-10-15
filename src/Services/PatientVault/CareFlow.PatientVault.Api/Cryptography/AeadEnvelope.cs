using System.Security.Cryptography;

namespace CareFlow.PatientVault.Api.Cryptography;

/// <summary>
/// AES-256-GCM com nonce aleatório de 96 bits. Formato: <c>versão(1) | nonce(12) | cifrado | tag(16)</c>.
/// O byte de versão permite trocar o algoritmo no futuro sem ambiguidade na leitura.
/// </summary>
/// <remarks>
/// Nonce aleatório é seguro até cerca de 2^32 cifragens por chave (limite do NIST SP 800-38D),
/// muito acima do volume de uma clínica entre rotações de chave.
/// </remarks>
internal static class AeadEnvelope
{
    public const byte FormatVersion = 1;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Overhead = 1 + NonceSize + TagSize;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var output = new byte[Overhead + plaintext.Length];
        output[0] = FormatVersion;

        var nonce = output.AsSpan(1, NonceSize);
        var ciphertext = output.AsSpan(1 + NonceSize, plaintext.Length);
        var tag = output.AsSpan(1 + NonceSize + plaintext.Length, TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    /// <summary>Lança <see cref="CryptographicException"/> se o conteúdo, a chave ou o contexto não baterem.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> associatedData)
    {
        if (envelope.Length < Overhead || envelope[0] != FormatVersion)
        {
            throw new CryptographicException("Conteúdo cifrado em formato desconhecido.");
        }

        var length = envelope.Length - Overhead;
        var plaintext = new byte[length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            envelope.Slice(1, NonceSize),
            envelope.Slice(1 + NonceSize, length),
            envelope.Slice(1 + NonceSize + length, TagSize),
            plaintext,
            associatedData);
        return plaintext;
    }
}
