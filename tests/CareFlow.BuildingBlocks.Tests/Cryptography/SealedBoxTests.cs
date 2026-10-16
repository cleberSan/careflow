using System.Security.Cryptography;
using System.Text;

using CareFlow.BuildingBlocks.Cryptography;

namespace CareFlow.BuildingBlocks.Tests.Cryptography;

public sealed class SealedBoxTests
{
    private const string Context = "careflow:test|pedido-1|patient-vault";

    [Fact]
    public void Seal_ThenOpenWithRecipientKey_ReturnsPlaintext()
    {
        // Arrange
        using var recipient = SealedBox.CreateKeyPair();
        var plaintext = Encoding.UTF8.GetBytes("{\"nome\":\"Paciente Sintético\"}");

        // Act
        var sealedBox = SealedBox.Seal(SealedBox.ExportPublicKey(recipient), plaintext, Context);
        var opened = SealedBox.Open(recipient, sealedBox, Context);

        // Assert
        Assert.Equal(plaintext, opened);
        Assert.DoesNotContain("Sint", Encoding.UTF8.GetString(Convert.FromBase64String(sealedBox)), StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_SamePlaintextTwice_ProducesDifferentEnvelopes()
    {
        // Arrange
        using var recipient = SealedBox.CreateKeyPair();
        var publicKey = SealedBox.ExportPublicKey(recipient);

        // Act
        var first = SealedBox.Seal(publicKey, "123456"u8, Context);
        var second = SealedBox.Seal(publicKey, "123456"u8, Context);

        // Assert: chave efêmera e nonce novos a cada envelope.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Open_WithOtherKey_Throws()
    {
        // Arrange
        using var recipient = SealedBox.CreateKeyPair();
        using var intruder = SealedBox.CreateKeyPair();
        var sealedBox = SealedBox.Seal(SealedBox.ExportPublicKey(recipient), "segredo"u8, Context);

        // Act + Assert
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(intruder, sealedBox, Context));
    }

    [Fact]
    public void Open_WithOtherContext_Throws()
    {
        // Arrange: a seção de um pedido não abre como seção de outro.
        using var recipient = SealedBox.CreateKeyPair();
        var sealedBox = SealedBox.Seal(SealedBox.ExportPublicKey(recipient), "segredo"u8, Context);

        // Act + Assert
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(recipient, sealedBox, "careflow:test|pedido-2|patient-vault"));
    }

    [Fact]
    public void Open_TamperedCiphertext_Throws()
    {
        // Arrange
        using var recipient = SealedBox.CreateKeyPair();
        var bytes = Convert.FromBase64String(SealedBox.Seal(SealedBox.ExportPublicKey(recipient), "segredo"u8, Context));
        bytes[^20] ^= 0x01;

        // Act + Assert
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(recipient, Convert.ToBase64String(bytes), Context));
    }

    [Theory]
    [InlineData("não é base64")]
    [InlineData("AQ==")]
    [InlineData("AgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Open_MalformedEnvelope_Throws(string envelope)
    {
        // Arrange
        using var recipient = SealedBox.CreateKeyPair();

        // Act + Assert
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(recipient, envelope, Context));
    }

    [Fact]
    public void Open_TruncatedEnvelope_Throws()
    {
        // Arrange: o tamanho declarado da chave efêmera passa do fim do envelope.
        using var recipient = SealedBox.CreateKeyPair();
        var bytes = Convert.FromBase64String(SealedBox.Seal(SealedBox.ExportPublicKey(recipient), "x"u8, Context));
        bytes[1] = 0xFF;

        // Act + Assert
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Open(recipient, Convert.ToBase64String(bytes), Context));
    }

    [Fact]
    public void ImportPrivateKey_RoundTrips()
    {
        // Arrange
        using var original = SealedBox.CreateKeyPair();
        var pkcs8 = Convert.ToBase64String(original.ExportPkcs8PrivateKey());
        var sealedBox = SealedBox.Seal(SealedBox.ExportPublicKey(original), "abc"u8, Context);

        // Act
        using var imported = SealedBox.ImportPrivateKey(pkcs8);

        // Assert
        Assert.Equal("abc"u8.ToArray(), SealedBox.Open(imported, sealedBox, Context));
    }

    [Fact]
    public void ImportPrivateKey_OtherCurve_Throws()
    {
        // Arrange
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);

        // Act + Assert
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.ImportPrivateKey(Convert.ToBase64String(p384.ExportPkcs8PrivateKey())));
    }

    [Fact]
    public void Seal_ToOtherCurve_Throws()
    {
        // Arrange
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);

        // Act + Assert
        Assert.ThrowsAny<CryptographicException>(() => SealedBox.Seal(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()), "x"u8, Context));
    }
}
