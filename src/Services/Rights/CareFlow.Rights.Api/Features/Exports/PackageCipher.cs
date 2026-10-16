using System.Security.Cryptography;

using CareFlow.Rights.Api.Common.Cryptography;

namespace CareFlow.Rights.Api.Features.Exports;

/// <summary>
/// Cifra do pacote guardado: uma chave AES-256 aleatória por pacote, ela mesma cifrada pela chave do serviço.
/// Apagar a chave cifrada (expiração) deixa o conteúdo ilegível mesmo que uma cópia do banco sobreviva.
/// </summary>
internal sealed class PackageCipher(RightsKeyRing keys)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public (byte[] Content, byte[] WrappedKey) Seal(Guid requestId, ReadOnlySpan<byte> zip)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var content = new byte[NonceSize + zip.Length + TagSize];
            var nonce = content.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(
                nonce,
                zip,
                content.AsSpan(NonceSize, zip.Length),
                content.AsSpan(NonceSize + zip.Length, TagSize),
                System.Text.Encoding.UTF8.GetBytes(RightsKeyRing.PackageContentContext(requestId)));
            return (content, keys.Wrap(key, RightsKeyRing.PackageKeyContext(requestId)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Open(Guid requestId, byte[] content, byte[] wrappedKey)
    {
        ArgumentNullException.ThrowIfNull(content);
        var key = keys.Unwrap(wrappedKey, RightsKeyRing.PackageKeyContext(requestId));
        try
        {
            var length = content.Length - NonceSize - TagSize;
            var zip = new byte[length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                content.AsSpan(0, NonceSize),
                content.AsSpan(NonceSize, length),
                content.AsSpan(NonceSize + length, TagSize),
                zip,
                System.Text.Encoding.UTF8.GetBytes(RightsKeyRing.PackageContentContext(requestId)));
            return zip;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
