using System.Security.Cryptography;
using System.Text;

using CareFlow.PatientVault.Api.Cryptography;

namespace CareFlow.PatientVault.UnitTests.Cryptography;

public sealed class TenantDataKeyTests
{
    private static TenantDataKey NewKey(Guid tenant, byte[]? dek = null) =>
        TenantDataKey.Derive(tenant, 1, dek ?? RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void EncryptThenDecrypt_SameContext_RoundTrips()
    {
        // Arrange
        var key = NewKey(Guid.NewGuid());
        var context = new FieldContext(Guid.NewGuid(), "full_name");

        // Act
        var envelope = key.Encrypt("Paciente Sintético", context);

        // Assert
        Assert.Equal("Paciente Sintético", key.Decrypt(envelope, context));
        Assert.DoesNotContain(Encoding.UTF8.GetBytes("Sintético"), Windows(envelope, Encoding.UTF8.GetByteCount("Sintético")));
    }

    [Fact]
    public void Decrypt_ValueMovedToAnotherPatient_Throws()
    {
        // Arrange
        var key = NewKey(Guid.NewGuid());
        var envelope = key.Encrypt("valor", new FieldContext(Guid.NewGuid(), "cpf"));

        // Act
        var act = () => key.Decrypt(envelope, new FieldContext(Guid.NewGuid(), "cpf"));

        // Assert
        Assert.ThrowsAny<CryptographicException>(act);
    }

    [Fact]
    public void Decrypt_ValueMovedToAnotherField_Throws()
    {
        // Arrange
        var key = NewKey(Guid.NewGuid());
        var patient = Guid.NewGuid();
        var envelope = key.Encrypt("valor", new FieldContext(patient, "email"));

        // Act
        var act = () => key.Decrypt(envelope, new FieldContext(patient, "phone"));

        // Assert
        Assert.ThrowsAny<CryptographicException>(act);
    }

    [Fact]
    public void Decrypt_WithSameDekButAnotherTenant_Throws()
    {
        // Arrange: mesmo material de chave, clínicas diferentes; o AAD amarra o dado à clínica.
        var dek = RandomNumberGenerator.GetBytes(32);
        var clinicA = NewKey(Guid.NewGuid(), (byte[])dek.Clone());
        var clinicB = NewKey(Guid.NewGuid(), dek);
        var context = new FieldContext(Guid.NewGuid(), "cpf");
        var envelope = clinicA.Encrypt("valor", context);

        // Act
        var act = () => clinicB.Decrypt(envelope, context);

        // Assert
        Assert.ThrowsAny<CryptographicException>(act);
    }

    [Fact]
    public void BlindIndex_IsDeterministicPerClinic_AndDiffersAcrossClinics()
    {
        // Arrange
        var clinicA = NewKey(Guid.NewGuid());
        var clinicB = NewKey(Guid.NewGuid());

        // Act
        var a1 = clinicA.BlindIndex("12345678909");
        var a2 = clinicA.BlindIndex("12345678909");
        var b = clinicB.BlindIndex("12345678909");

        // Assert
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.Equal(32, a1.Length);
    }

    [Fact]
    public void Derive_ZeroesTheReceivedDataKey()
    {
        // Arrange
        var dek = RandomNumberGenerator.GetBytes(32);

        // Act
        _ = TenantDataKey.Derive(Guid.NewGuid(), 1, dek);

        // Assert
        Assert.All(dek, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Derive_SameDek_IsReproducible()
    {
        // Arrange
        var dek = RandomNumberGenerator.GetBytes(32);
        var tenant = Guid.NewGuid();
        var context = new FieldContext(Guid.NewGuid(), "cpf");
        var envelope = TenantDataKey.Derive(tenant, 1, (byte[])dek.Clone()).Encrypt("valor", context);

        // Act
        var decrypted = TenantDataKey.Derive(tenant, 1, dek).Decrypt(envelope, context);

        // Assert
        Assert.Equal("valor", decrypted);
    }

    private static IEnumerable<byte[]> Windows(byte[] data, int size)
    {
        for (var i = 0; i + size <= data.Length; i++)
        {
            yield return data[i..(i + size)];
        }
    }
}
