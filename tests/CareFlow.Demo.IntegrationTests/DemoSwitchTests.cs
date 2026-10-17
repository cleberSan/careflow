using System.Net;
using System.Net.Http.Json;

using CareFlow.Demo.Api.Features;
using CareFlow.Demo.IntegrationTests.Infrastructure;

namespace CareFlow.Demo.IntegrationTests;

/// <summary>
/// Interruptor do Demo (ADR-0013): um arquivo fora da imagem, relido sem reiniciar. Desligado, o Demo recusa tudo
/// com 503 e Retry-After (o Painel cai para o replay), encerra as sessões abertas e apaga as clínicas delas.
/// </summary>
public sealed class DemoSwitchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_SaysAvailable_WhileTheSwitchIsOn()
    {
        // Arrange
        await using var demo = DemoHost.Start();
        using var visitor = demo.Visitor();

        // Act
        var status = await visitor.GetFromJsonAsync<StatusView>("demo/v1/status", Ct);

        // Assert
        Assert.Equal(new StatusView(true, "disponivel"), status);
    }

    [Fact]
    public async Task SwitchingOff_RefusesNewSessions_EndsOpenOnes_AndPurgesTheirClinics_WithoutRestart()
    {
        // Arrange
        await using var demo = DemoHost.Start();
        using var visitor = demo.Visitor();
        await DemoHost.OpenAsync(visitor);
        using var newcomer = demo.Visitor();

        // Act
        await demo.SwitchAsync(enabled: false);
        await Eventually.TrueAsync(
            async () => (await newcomer.GetFromJsonAsync<StatusView>("demo/v1/status", Ct))!.Available is false,
            "o Demo ler o interruptor desligado");
        using var refusedSession = await newcomer.PostAsync("demo/v1/sessions", null, Ct);
        using var refusedAction = await visitor.GetAsync("demo/v1/session", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refusedSession.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(300), refusedSession.Headers.RetryAfter?.Delta);
        Assert.Contains("Demo.Desligado", await refusedSession.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refusedAction.StatusCode);
        await Eventually.TrueAsync(() => demo.CareFlow.PurgeCount >= 6, "a clínica da sessão aberta ser apagada nos seis serviços");
    }

    [Fact]
    public async Task SwitchingBackOn_AcceptsSessionsAgain()
    {
        // Arrange
        await using var demo = DemoHost.Start();
        using var visitor = demo.Visitor();
        await demo.SwitchAsync(enabled: false);
        await Eventually.TrueAsync(
            async () => (await visitor.GetFromJsonAsync<StatusView>("demo/v1/status", Ct))!.State == "desligado",
            "o Demo ler o interruptor desligado");

        // Act
        await demo.SwitchAsync(enabled: true);
        await Eventually.TrueAsync(
            async () => (await visitor.GetFromJsonAsync<StatusView>("demo/v1/status", Ct))!.Available,
            "o Demo ler o interruptor ligado");
        var session = await DemoHost.OpenAsync(visitor);

        // Assert
        Assert.StartsWith("isolated-", session.Partition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSwitchFile_KeepsTheDemoOn()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("careflow-demo-it-").FullName;
        await using var demo = DemoHost.Start(directory, ("Demo:SwitchFile", Path.Combine(directory, "nao-existe.json")));
        using var visitor = demo.Visitor();

        // Act
        var status = await visitor.GetFromJsonAsync<StatusView>("demo/v1/status", Ct);

        // Assert
        Assert.True(status!.Available);
    }

    [Fact]
    public async Task FullPool_IsReportedByTheStatus()
    {
        // Arrange
        await using var demo = DemoHost.Start(("Demo:Slots", "1"));
        using var visitor = demo.Visitor();
        await DemoHost.OpenAsync(visitor);

        // Act
        var status = await visitor.GetFromJsonAsync<StatusView>("demo/v1/status", Ct);

        // Assert
        Assert.Equal(new StatusView(true, "lotado"), status);
    }
}
