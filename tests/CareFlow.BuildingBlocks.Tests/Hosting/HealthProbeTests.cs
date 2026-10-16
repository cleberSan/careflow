using System.Net;

using CareFlow.BuildingBlocks.AspNetCore.Hosting;

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CareFlow.BuildingBlocks.Tests.Hosting;

public sealed class HealthProbeTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, 0)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    public async Task Probe_ExitsWithZeroOnlyWhenHealthy(HttpStatusCode status, int expected)
    {
        // Arrange
        using var handler = new StubHandler(_ => new HttpResponseMessage(status));

        // Act
        var exit = await HealthProbe.RunAsync([HealthProbe.Argument, "http://127.0.0.1:8080/health/ready"], handler);

        // Assert
        Assert.Equal(expected, exit);
        Assert.Equal("/health/ready", handler.Seen.Single().AbsolutePath);
    }

    [Fact]
    public async Task Probe_TreatsAConnectionFailureAsUnhealthy()
    {
        // Arrange
        using var handler = new StubHandler(_ => throw new HttpRequestException("recusada"));

        // Act
        var exit = await HealthProbe.RunAsync([HealthProbe.Argument, "http://localhost:8080/health/live"], handler);

        // Assert
        Assert.Equal(1, exit);
    }

    [Theory]
    [InlineData("http://exemplo.com/health/ready")]
    [InlineData("https://127.0.0.1:8080/health/ready")]
    [InlineData("nao-e-url")]
    public async Task Probe_RefusesAnythingButPlainLoopbackHttp(string target)
    {
        // Arrange
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var exit = await HealthProbe.RunAsync([HealthProbe.Argument, target], handler);

        // Assert
        Assert.Equal(2, exit);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public void IsRequested_OnlyWithTheArgumentFirst()
    {
        Assert.True(HealthProbe.IsRequested([HealthProbe.Argument, "http://127.0.0.1/"]));
        Assert.False(HealthProbe.IsRequested([]));
        Assert.False(HealthProbe.IsRequested(["--urls", HealthProbe.Argument]));
    }

    [Theory]
    [InlineData("Production", false, true)]
    [InlineData("Demo", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Testing", false, false)]
    public void Environments_HostedDemoIsInternetFacingLikeProduction(string name, bool hostedDemo, bool internetFacing)
    {
        // Arrange
        var environment = new Environment(name);

        // Act & Assert
        Assert.Equal(hostedDemo, environment.IsHostedDemo());
        Assert.Equal(internetFacing, environment.IsInternetFacing());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "teste";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
