using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CareFlow.BuildingBlocks.Cryptography;

/// <summary>
/// Cifra para uma chave pública (ECIES com P-256): uma chave efêmera por mensagem, ECDH com a chave do destinatário,
/// HKDF-SHA256 e AES-256-GCM. Só quem tem a chave privada abre. Usado quando um dado pessoal precisa passar por
/// Outbox, broker e Inbox (que guardam as mensagens por dias) sem ficar legível em nenhum deles: a seção de um
/// serviço no pacote do titular e o código de confirmação de identidade (ADR-0012).
/// </summary>
/// <remarks>
/// Formato (Base64): <c>versão(1) | tamanho da chave efêmera(2) | chave efêmera (SPKI) | nonce(12) | cifrado | tag(16)</c>.
/// O <c>context</c> entra como dado autenticado: uma seção copiada para outro pedido ou outro serviço não abre.
/// </remarks>
public static class SealedBox
{
    public const byte FormatVersion = 1;

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("careflow:sealed-box:v1");

    /// <summary>Gera um par de chaves P-256. A pública sai em SPKI (Base64) para ir num evento ou numa configuração.</summary>
    public static ECDiffieHellman CreateKeyPair() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static string ExportPublicKey(ECDiffieHellman key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Lê uma chave privada PKCS#8 (Base64). Lança <see cref="CryptographicException"/> se não for P-256.</summary>
    public static ECDiffieHellman ImportPrivateKey(string pkcs8Base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pkcs8Base64);
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8Base64), out _);
            EnsureP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static string Seal(string recipientPublicKey, ReadOnlySpan<byte> plaintext, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientPublicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(Convert.FromBase64String(recipientPublicKey), out _);
        EnsureP256(recipient);

        using var ephemeral = CreateKeyPair();
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var key = DeriveKey(ephemeral.DeriveRawSecretAgreement(recipient.PublicKey), ephemeralPublic);
        try
        {
            var output = new byte[1 + 2 + ephemeralPublic.Length + NonceSize + plaintext.Length + TagSize];
            output[0] = FormatVersion;
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(1, 2), (ushort)ephemeralPublic.Length);
            ephemeralPublic.CopyTo(output.AsSpan(3));

            var offset = 3 + ephemeralPublic.Length;
            var nonce = output.AsSpan(offset, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(offset + NonceSize, plaintext.Length),
                output.AsSpan(offset + NonceSize + plaintext.Length, TagSize),
                Encoding.UTF8.GetBytes(context));
            return Convert.ToBase64String(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Abre o envelope. Chave errada, contexto errado ou conteúdo alterado lançam <see cref="CryptographicException"/>.</summary>
    public static byte[] Open(ECDiffieHellman recipient, string sealedBox, string context)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(sealedBox);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        byte[] input;
        try
        {
            input = Convert.FromBase64String(sealedBox);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Envelope em formato desconhecido.", ex);
        }

        if (input.Length < 3 + NonceSize + TagSize || input[0] != FormatVersion)
        {
            throw new CryptographicException("Envelope em formato desconhecido.");
        }

        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(1, 2));
        var offset = 3 + keyLength;
        var length = input.Length - offset - NonceSize - TagSize;
        if (length < 0)
        {
            throw new CryptographicException("Envelope truncado.");
        }

        var ephemeralPublic = input.AsSpan(3, keyLength).ToArray();
        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(ephemeralPublic, out _);
        EnsureP256(ephemeral);

        var key = DeriveKey(recipient.DeriveRawSecretAgreement(ephemeral.PublicKey), ephemeralPublic);
        try
        {
            var plaintext = new byte[length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                input.AsSpan(offset, NonceSize),
                input.AsSpan(offset + NonceSize, length),
                input.AsSpan(offset + NonceSize + length, TagSize),
                plaintext,
                Encoding.UTF8.GetBytes(context));
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // A chave efêmera entra no salt: cada mensagem deriva uma chave AES diferente mesmo que o segredo se repetisse.
    private static byte[] DeriveKey(byte[] sharedSecret, byte[] ephemeralPublic)
    {
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, KeySize, salt: ephemeralPublic, info: Info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private static void EnsureP256(ECDiffieHellman key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (parameters.Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            throw new CryptographicException("A chave precisa ser P-256.");
        }
    }
}
