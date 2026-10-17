using CareFlow.Triage.Api.Models;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CareFlow.Triage.IntegrationTests.Models;

/// <summary>Na demonstração pública o modelo real só liga com o interruptor explícito (ADR-0013).</summary>
public sealed class LiveCallsGuardTests
{
    [Theory]
    [InlineData("Demo", false, false)]
    [InlineData("Production", false, false)]
    [InlineData("Demo", true, true)]
    [InlineData("Development", false, true)]
    public void AnthropicProvider_InAnInternetFacingEnvironment_NeedsTheLiveCallsSwitch(string environment, bool liveCalls, bool accepted)
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Triage:Model:Provider"] = "Anthropic",
                ["Triage:Model:LiveCallsEnabled"] = liveCalls.ToString(),
                ["ANTHROPIC_API_KEY"] = "chave-de-teste",
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddTriageModel(configuration, new FakeEnvironment(environment))
            .BuildServiceProvider();

        // Act
        var exception = Record.Exception(() => provider.GetRequiredService<IOptions<TriageModelOptions>>().Value);

        // Assert
        if (accepted)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Contains("LiveCallsEnabled", Assert.IsType<OptionsValidationException>(exception).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RecordedProvider_IsTheDefaultPathOfThePublicDemo()
    {
        // Arrange
        var recordings = Directory.CreateTempSubdirectory("careflow-triage-guard-").FullName;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Triage:Model:Provider"] = "Recorded",
                ["Triage:Model:RecordingsPath"] = recordings,
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddTriageModel(configuration, new FakeEnvironment("Demo"))
            .BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<TriageModelOptions>>().Value;

        // Assert
        Assert.Equal("Recorded", options.Provider);
        Assert.False(options.LiveCallsEnabled);
        Directory.Delete(recordings);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CareFlow.Triage.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
