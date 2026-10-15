using System.Security.Cryptography;
using System.Text;

using CareFlow.PatientVault.Api.Cryptography;

namespace CareFlow.PatientVault.UnitTests.Cryptography;

public sealed class AeadEnvelopeTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("contexto");

    [Theory]
    [InlineData("")]
    [InlineData("Paciente Sintetico")]
    public void SealThenOpen_RoundTrips(string text)
    {
        // Arrange
        var plaintext = Encoding.UTF8.GetBytes(text);

        // Act
        var envelope = AeadEnvelope.Seal(Key, plaintext, Aad);
        var opened = AeadEnvelope.Open(Key, envelope, Aad);

        // Assert
        Assert.Equal(plaintext, opened);
        Assert.Equal(AeadEnvelope.Overhead + plaintext.Length, envelope.Length);
        Assert.Equal(AeadEnvelope.FormatVersion, envelope[0]);
    }

    [Fact]
    public void Seal_SamePlaintextTwice_ProducesDifferentCiphertexts()
    {
        // Arrange
        var plaintext = Encoding.UTF8.GetBytes("mesmo valor");

        // Act
        var first = AeadEnvelope.Seal(Key, plaintext, Aad);
        var second = AeadEnvelope.Seal(Key, plaintext, Aad);

        // Assert: nonce aleatório, então o banco não revela valores repetidos.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Open_TamperedCiphertext_Throws()
    {
        // Arrange
        var envelope = AeadEnvelope.Seal(Key, Encoding.UTF8.GetBytes("valor"), Aad);
        envelope[1 + AeadEnvelope.NonceSize] ^= 0x01;

        // Act
        var act = () => AeadEnvelope.Open(Key, envelope, Aad);

        // Assert
        Assert.ThrowsAny<CryptographicException>(act);
    }

    [Fact]
    public void Open_WrongAssociatedData_Throws()
    {
        // Arrange
        var envelope = AeadEnvelope.Seal(Key, Encoding.UTF8.GetBytes("valor"), Aad);

        // Act
        var act = () => AeadEnvelope.Open(Key, envelope, Encoding.UTF8.GetBytes("outro contexto"));

        // Assert
        Assert.ThrowsAny<CryptographicException>(act);
    }

    [Fact]
    public void Open_WrongKey_Throws()
    {
        // Arrange
        var envelope = AeadEnvelope.Seal(Key, Encoding.UTF8.GetBytes("valor"), Aad);

        // Act
        var act = () => AeadEnvelope.Open(RandomNumberGenerator.GetBytes(32), envelope, Aad);

        // Assert
        Assert.ThrowsAny<CryptographicException>(act);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AeadEnvelope.Overhead - 1)]
    public void Open_TooShort_Throws(int length)
    {
        // Act
        var act = () => AeadEnvelope.Open(Key, new byte[length], Aad);

        // Assert
        Assert.ThrowsAny<CryptographicException>(act);
    }

    [Fact]
    public void Open_UnknownFormatVersion_Throws()
    {
        // Arrange
        var envelope = AeadEnvelope.Seal(Key, Encoding.UTF8.GetBytes("valor"), Aad);
        envelope[0] = 99;

        // Act
        var act = () => AeadEnvelope.Open(Key, envelope, Aad);

        // Assert
        Assert.ThrowsAny<CryptographicException>(act);
    }
}
