using CareFlow.BuildingBlocks.Persistence.Demo;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CareFlow.BuildingBlocks.Tests.Persistence;

public sealed class DemoPurgeTests
{
    [Fact]
    public void DeletionOrder_PutsReferencingTablesBeforeReferencedOnes()
    {
        // Arrange: notifications <- delivery_attempts; notifications <- notification_jobs; outbox solta.
        var tables = new HashSet<string>(StringComparer.Ordinal) { "notifications", "delivery_attempts", "notification_jobs", "outbox_messages" };
        (string, string)[] references = [("delivery_attempts", "notifications"), ("notification_jobs", "notifications"), ("x", "notifications")];

        // Act
        var order = TenantDataPurge.DeletionOrder(tables, references);

        // Assert
        Assert.Equal(4, order.Count);
        Assert.True(order.ToList().IndexOf("notifications") > order.ToList().IndexOf("delivery_attempts"));
        Assert.True(order.ToList().IndexOf("notifications") > order.ToList().IndexOf("notification_jobs"));
    }

    [Fact]
    public void DeletionOrder_WithACycle_StillListsEveryTableOnce()
    {
        // Arrange
        var tables = new HashSet<string>(StringComparer.Ordinal) { "a", "b", "c" };
        (string, string)[] references = [("a", "b"), ("b", "a"), ("c", "a")];

        // Act
        var order = TenantDataPurge.DeletionOrder(tables, references);

        // Assert
        Assert.Equal(["a", "b", "c"], order.Order(StringComparer.Ordinal));
        Assert.Equal("c", order[0]);
    }

    [Theory]
    [InlineData("Development", "true", true)]
    [InlineData("Testing", "true", true)]
    [InlineData("Development", "false", false)]
    [InlineData("Production", null, false)]
    [InlineData("Production", "false", false)]
    public void IsEnabled_FollowsTheSetting_OutsideProduction(string environment, string? setting, bool expected)
    {
        // Arrange
        var configuration = Configuration(setting);

        // Act
        var enabled = DemoPurgeEndpoint.IsEnabled(configuration, new Environment(environment));

        // Assert
        Assert.Equal(expected, enabled);
    }

    [Fact]
    public void IsEnabled_InProduction_RefusesToStart()
    {
        // Act
        var error = Assert.Throws<InvalidOperationException>(() => DemoPurgeEndpoint.IsEnabled(Configuration("true"), new Environment("Production")));

        // Assert
        Assert.Contains(DemoPurgeEndpoint.EnabledSetting, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purge_RejectsAnEmptyClinic_BeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            TenantDataPurge.PurgeAsync("Host=nowhere.invalid", Guid.Empty, [], TestContext.Current.CancellationToken));
    }

    private static IConfiguration Configuration(string? setting) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DemoPurgeEndpoint.EnabledSetting] = setting })
            .Build();

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = "/";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
