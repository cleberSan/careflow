using System.Security.Cryptography;

using CareFlow.PatientVault.Api.Cryptography.KeyManagement;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CareFlow.PatientVault.UnitTests.Cryptography;

public sealed class KeyManagementExtensionsTests
{
    private static IConfiguration Config(string? provider, string endpoint = "https://crypto.exemplo/", string keyId = "ocid1.key.oc1.exemplo") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeyManagement:Provider"] = provider,
                ["KeyManagement:Local:ActiveKeyId"] = "k1",
                ["KeyManagement:Local:Keys:k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["KeyManagement:OciVault:CryptoEndpoint"] = endpoint,
                ["KeyManagement:OciVault:KeyId"] = keyId,
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

    [Theory]
    [InlineData("Production")]
    [InlineData("Demo")]
    public void Local_InAnInternetFacingEnvironment_IsRefused(string environment)
    {
        // Act
        var act = () => new ServiceCollection().AddKeyManagement(Config("Local"), new FakeEnvironment(environment));

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void OciVault_InTheHostedDemo_RegistersTheVaultProvider()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddKeyManagement(Config("OciVault"), new FakeEnvironment("Demo"));
        using var provider = services.BuildServiceProvider();
        provider.ValidateKeyManagementOnStart();

        // Assert
        Assert.IsType<OciVaultKeyManagementService>(provider.GetRequiredService<IKeyManagementService>());
    }

    [Theory]
    [InlineData("http://crypto.exemplo/", "ocid1.key.oc1.exemplo")]
    [InlineData("https://crypto.exemplo/", "chave-qualquer")]
    [InlineData("https://crypto.exemplo/", "")]
    public void OciVault_WithInsecureEndpointOrInvalidKey_FailsValidation(string endpoint, string keyId)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddKeyManagement(Config("OciVault", endpoint, keyId), new FakeEnvironment("Demo"));
        using var provider = services.BuildServiceProvider();

        // Act
        var act = () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OciVaultKeyManagementOptions>>().Value;

        // Assert
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(act);
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
