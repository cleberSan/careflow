using CareFlow.BuildingBlocks.Diagnostics;

namespace CareFlow.BuildingBlocks.Tests.Diagnostics;

public sealed class LogMaskTests
{
    [Fact]
    public void Pseudonym_KeepsOnlyFourHexDigits()
    {
        // Arrange
        var value = Guid.Parse("3f2a9c1e-0000-4000-8000-000000000001");

        // Act
        var masked = LogMask.Pseudonym(value);

        // Assert
        Assert.Equal("3f2a…", masked);
        Assert.DoesNotContain(value.ToString("N")[4..], masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Pseudonym_OfEmpty_SaysSo() => Assert.Equal("(vazio)", LogMask.Pseudonym(Guid.Empty));
}
