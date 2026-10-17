using System.Security.Cryptography;
using System.Text;

using CareFlow.PatientVault.Api.Cryptography.KeyManagement;

using Microsoft.Extensions.Options;

namespace CareFlow.PatientVault.UnitTests.Cryptography;

public sealed class OciVaultKeyManagementServiceTests
{
    private const string KeyId = "ocid1.key.oc1.sa-saopaulo-1.exemplo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OciVaultKeyManagementService Kms(FakeVault vault, string keyId = KeyId) =>
        new(vault, Options.Create(new OciVaultKeyManagementOptions { CryptoEndpoint = new Uri("https://crypto.exemplo"), KeyId = keyId }));

    [Fact]
    public async Task WrapThenUnwrap_ReturnsTheSameKey_AndStoresTheVaultKeyWithPrefix()
    {
        // Arrange
        var vault = new FakeVault();
        var kms = Kms(vault);
        var dek = RandomNumberGenerator.GetBytes(32);
        var context = new KeyWrapContext(Guid.NewGuid(), 1);

        // Act
        var wrapped = await kms.WrapKeyAsync(dek, context, Ct);
        var unwrapped = await kms.UnwrapKeyAsync(wrapped, context, Ct);

        // Assert
        Assert.Equal("oci:" + KeyId, wrapped.KeyEncryptionKeyId);
        Assert.Equal(dek, unwrapped);
        Assert.DoesNotContain(Convert.ToBase64String(dek), Encoding.UTF8.GetString(wrapped.Ciphertext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_GoesToTheVaultAsAssociatedData_SoAnotherClinicCannotOpenIt()
    {
        // Arrange
        var vault = new FakeVault();
        var kms = Kms(vault);
        var clinic = Guid.NewGuid();
        var wrapped = await kms.WrapKeyAsync(RandomNumberGenerator.GetBytes(32), new KeyWrapContext(clinic, 1), Ct);

        // Act
        var otherClinic = () => kms.UnwrapKeyAsync(wrapped, new KeyWrapContext(Guid.NewGuid(), 1), Ct);
        var otherVersion = () => kms.UnwrapKeyAsync(wrapped, new KeyWrapContext(clinic, 2), Ct);

        // Assert
        Assert.Contains(clinic.ToString("N"), Assert.Single(vault.SeenContexts), StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<CryptographicException>(otherClinic);
        await Assert.ThrowsAnyAsync<CryptographicException>(otherVersion);
    }

    [Fact]
    public async Task OldKey_StillDecrypts_AfterTheActiveKeyChanges()
    {
        // Arrange
        var vault = new FakeVault();
        var context = new KeyWrapContext(Guid.NewGuid(), 1);
        var dek = RandomNumberGenerator.GetBytes(32);
        var wrapped = await Kms(vault).WrapKeyAsync(dek, context, Ct);

        // Act
        var rotated = Kms(vault, "ocid1.key.oc1.sa-saopaulo-1.nova");
        var unwrapped = await rotated.UnwrapKeyAsync(wrapped, context, Ct);
        var rewrapped = await rotated.WrapKeyAsync(dek, context, Ct);

        // Assert
        Assert.Equal(dek, unwrapped);
        Assert.Equal("oci:ocid1.key.oc1.sa-saopaulo-1.nova", rewrapped.KeyEncryptionKeyId);
    }

    [Fact]
    public async Task KeyWrappedByTheLocalProvider_IsRefused()
    {
        // Arrange
        var kms = Kms(new FakeVault());

        // Act
        var act = () => kms.UnwrapKeyAsync(new WrappedKey("dev-1", RandomNumberGenerator.GetBytes(60)), new KeyWrapContext(Guid.NewGuid(), 1), Ct);

        // Assert
        await Assert.ThrowsAsync<CryptographicException>(act);
    }

    [Fact]
    public async Task MalformedAnswerFromTheVault_IsACryptographicFailure()
    {
        // Arrange
        var kms = Kms(new FakeVault { Corrupt = true });

        // Act
        var act = () => kms.WrapKeyAsync(RandomNumberGenerator.GetBytes(32), new KeyWrapContext(Guid.NewGuid(), 1), Ct);

        // Assert
        await Assert.ThrowsAsync<CryptographicException>(act);
    }

    /// <summary>Vault de mentira: AES-GCM por OCID, com o dado associado amarrado como o OCI Vault faz.</summary>
    private sealed class FakeVault : IOciCrypto
    {
        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

        public bool Corrupt { get; init; }

        public List<string> SeenContexts { get; } = [];

        public Task<string> EncryptAsync(string keyId, string plaintextBase64, IReadOnlyDictionary<string, string> associatedData, CancellationToken cancellationToken)
        {
            SeenContexts.Add(string.Join('|', associatedData.Select(pair => $"{pair.Key}={pair.Value}")));
            if (Corrupt)
            {
                return Task.FromResult("não é base64");
            }

            var nonce = RandomNumberGenerator.GetBytes(12);
            var plaintext = Convert.FromBase64String(plaintextBase64);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key(keyId), 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(associatedData));
            return Task.FromResult(Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]));
        }

        public Task<string> DecryptAsync(string keyId, string ciphertextBase64, IReadOnlyDictionary<string, string> associatedData, CancellationToken cancellationToken)
        {
            var blob = Convert.FromBase64String(ciphertextBase64);
            var plaintext = new byte[blob.Length - 28];
            using var aes = new AesGcm(Key(keyId), 16);
            aes.Decrypt(blob[..12], blob[28..], blob[12..28], plaintext, Aad(associatedData));
            return Task.FromResult(Convert.ToBase64String(plaintext));
        }

        private byte[] Key(string keyId)
        {
            if (!_keys.TryGetValue(keyId, out var key))
            {
                key = _keys[keyId] = RandomNumberGenerator.GetBytes(32);
            }

            return key;
        }

        private static byte[] Aad(IReadOnlyDictionary<string, string> data) =>
            Encoding.UTF8.GetBytes(string.Join('|', data.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")));
    }
}
