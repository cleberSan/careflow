using System.Security.Cryptography;

using CareFlow.PatientVault.Api.Cryptography.KeyManagement;

using Microsoft.Extensions.Options;

namespace CareFlow.PatientVault.UnitTests.Cryptography;

public sealed class LocalKeyManagementServiceTests
{
    private static readonly string KeyA = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string KeyB = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static LocalKeyManagementService Kms(string active, params (string Id, string Key)[] keys) =>
        new(Options.Create(new LocalKeyManagementOptions
        {
            ActiveKeyId = active,
            Keys = keys.ToDictionary(k => k.Id, k => k.Key),
        }));

    [Fact]
    public async Task WrapThenUnwrap_SameContext_ReturnsDataKey()
    {
        // Arrange
        var kms = Kms("a", ("a", KeyA));
        var dek = RandomNumberGenerator.GetBytes(32);
        var context = new KeyWrapContext(Guid.NewGuid(), 1);

        // Act
        var wrapped = await kms.WrapKeyAsync(dek, context, TestContext.Current.CancellationToken);
        var unwrapped = await kms.UnwrapKeyAsync(wrapped, context, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("a", wrapped.KeyEncryptionKeyId);
        Assert.Equal(dek, unwrapped);
        Assert.NotEqual(dek, wrapped.Ciphertext[..32]);
    }

    [Fact]
    public async Task Unwrap_WithAnotherTenantContext_Throws()
    {
        // Arrange
        var kms = Kms("a", ("a", KeyA));
        var wrapped = await kms.WrapKeyAsync(RandomNumberGenerator.GetBytes(32), new KeyWrapContext(Guid.NewGuid(), 1), TestContext.Current.CancellationToken);

        // Act
        var act = () => kms.UnwrapKeyAsync(wrapped, new KeyWrapContext(Guid.NewGuid(), 1), TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAnyAsync<CryptographicException>(act);
    }

    [Fact]
    public async Task Unwrap_AfterKekRotation_StillReadsKeysWrappedByOldKek()
    {
        // Arrange
        var context = new KeyWrapContext(Guid.NewGuid(), 1);
        var dek = RandomNumberGenerator.GetBytes(32);
        var wrappedByA = await Kms("a", ("a", KeyA)).WrapKeyAsync(dek, context, TestContext.Current.CancellationToken);
        var rotated = Kms("b", ("a", KeyA), ("b", KeyB));

        // Act
        var unwrapped = await rotated.UnwrapKeyAsync(wrappedByA, context, TestContext.Current.CancellationToken);
        var rewrapped = await rotated.WrapKeyAsync(dek, context, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(dek, unwrapped);
        Assert.Equal("b", rewrapped.KeyEncryptionKeyId);
    }

    [Fact]
    public async Task Unwrap_UnknownKek_Throws()
    {
        // Arrange
        var context = new KeyWrapContext(Guid.NewGuid(), 1);
        var wrapped = await Kms("a", ("a", KeyA)).WrapKeyAsync(RandomNumberGenerator.GetBytes(32), context, TestContext.Current.CancellationToken);

        // Act
        var act = () => Kms("b", ("b", KeyB)).UnwrapKeyAsync(wrapped, context, TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAnyAsync<CryptographicException>(act);
    }

    [Fact]
    public async Task Operations_HonorCancellation()
    {
        // Arrange
        var kms = Kms("a", ("a", KeyA));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var context = new KeyWrapContext(Guid.NewGuid(), 1);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kms.WrapKeyAsync(new byte[32], context, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kms.UnwrapKeyAsync(new WrappedKey("a", new byte[64]), context, cts.Token));
    }

    [Theory]
    [InlineData("", "a")]
    [InlineData("b", "a")]
    public void Constructor_ActiveKeyMissing_Throws(string active, string configured)
    {
        // Act
        var act = () => Kms(active, (configured, KeyA));

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    [Theory]
    [InlineData("nao-e-base64!")]
    [InlineData("AAAA")]
    public void Constructor_InvalidKeyMaterial_Throws(string key)
    {
        // Act
        var act = () => Kms("a", ("a", key));

        // Assert
        var error = Assert.Throws<InvalidOperationException>(act);
        Assert.DoesNotContain(key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        // Assert
        Assert.Throws<ArgumentNullException>(() => new LocalKeyManagementService(null!));
    }
}
