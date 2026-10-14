using System.Security.Cryptography;

using CareFlow.PatientVault.Api.Cryptography.KeyManagement;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CareFlow.PatientVault.UnitTests.Cryptography;

public sealed class KeyManagementExtensionsTests
{
    private static IConfiguration Config(string? provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeyManagement:Provider"] = provider,
                ["KeyManagement:Local:ActiveKeyId"] = "k1",
                ["KeyManagement:Local:Keys:k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            })
            .Build();

    [Fact]
    public void Local_InDevelopment_RegistersWorkingKms()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddKeyManagement(Config("Local"), new FakeEnvironment(Environments.Development));
        using var provider = services.BuildServiceProvider();
        provider.ValidateKeyManagementOnStart();

        // Assert
        Assert.IsType<LocalKeyManagementService>(provider.GetRequiredService<IKeyManagementService>());
    }

    [Fact]
    public void Local_InProduction_IsRefused()
    {
        // Act
        var act = () => new ServiceCollection().AddKeyManagement(Config("Local"), new FakeEnvironment(Environments.Production));

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("AzureKeyVault")]
    public void MissingOrUnknownProvider_IsRefused(string? provider)
    {
        // Act
        var act = () => new ServiceCollection().AddKeyManagement(Config(provider), new FakeEnvironment(Environments.Development));

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CareFlow.PatientVault.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
