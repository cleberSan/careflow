using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CareFlow.Scheduling.IntegrationTests.Appointments;

public sealed class RowLevelSecurityMigrationSqlTests
{
    [Theory]
    [InlineData("appointments; DROP TABLE x")]
    [InlineData("Appointments")]
    [InlineData("app\"ointments")]
    [InlineData(" ")]
    public void InvalidIdentifier_IsRejectedInsteadOfBecomingSql(string table)
    {
        // Arrange
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");

        // Act
        var act = () => builder.EnableTenantRowLevelSecurity(table, "scheduling_app");

        // Assert
        Assert.ThrowsAny<ArgumentException>(act);
        Assert.Empty(builder.Operations);
    }

    [Fact]
    public void Enable_ForcesRlsAndChecksWrites()
    {
        // Arrange
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");

        // Act
        builder.EnableTenantRowLevelSecurity("appointments", "scheduling_app", "SELECT");

        // Assert
        var sql = Assert.IsType<SqlOperation>(Assert.Single(builder.Operations)).Sql;
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("WITH CHECK", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON \"appointments\" TO \"scheduling_app\"", sql, StringComparison.Ordinal);
    }
}
