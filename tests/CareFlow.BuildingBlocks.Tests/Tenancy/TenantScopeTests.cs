using CareFlow.BuildingBlocks.Tenancy;

namespace CareFlow.BuildingBlocks.Tests.Tenancy;

public sealed class TenantScopeTests
{
    [Fact]
    public void New_HasNoTenant()
    {
        // Arrange
        var scope = new TenantScope();

        // Act
        var found = scope.TryGetTenantId(out var tenantId);

        // Assert
        Assert.False(found);
        Assert.False(scope.HasTenant);
        Assert.Equal(Guid.Empty, tenantId);
    }

    [Fact]
    public void Enter_SetsTenantAndAllowsReenteringSameTenant()
    {
        // Arrange
        var scope = new TenantScope();
        var clinic = Guid.NewGuid();

        // Act
        scope.Enter(clinic);
        scope.Enter(clinic);

        // Assert
        Assert.True(scope.TryGetTenantId(out var tenantId));
        Assert.Equal(clinic, tenantId);
    }

    [Fact]
    public void Enter_AnotherTenant_Throws()
    {
        // Arrange
        var scope = new TenantScope();
        scope.Enter(Guid.NewGuid());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => scope.Enter(Guid.NewGuid()));
    }

    [Fact]
    public void Enter_EmptyTenant_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new TenantScope().Enter(Guid.Empty));
    }
}
