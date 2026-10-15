using CareFlow.BuildingBlocks.Tenancy;

namespace CareFlow.BuildingBlocks.Tests.Tenancy;

public sealed class NoTenantContextTests
{
    [Fact]
    public void TenantId_Throws()
    {
        // Act
        var act = () => (object)NoTenantContext.Instance.TenantId;

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void TryGetTenantId_ReturnsFalseAndEmpty()
    {
        // Act
        var found = NoTenantContext.Instance.TryGetTenantId(out var tenantId);

        // Assert
        Assert.False(found);
        Assert.Equal(Guid.Empty, tenantId);
    }
}
