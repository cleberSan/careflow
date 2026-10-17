using System.Text.Json;

using CareFlow.Demo.Api.Sessions;
using CareFlow.Demo.IntegrationTests.Infrastructure;

namespace CareFlow.Demo.IntegrationTests;

/// <summary>
/// Limpeza garantida das clínicas de visitantes (ADR-0013): o registro em arquivo sobrevive ao reinício do Demo, e
/// o que sobrou é apagado na subida, antes de a partição voltar para o pool.
/// </summary>
public sealed class ClinicLedgerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenSession_IsRecorded_AndEndingItEmptiesTheLedgerAfterThePurge()
    {
        // Arrange
        await using var demo = DemoHost.Start();
        using var visitor = demo.Visitor();
        await DemoHost.OpenAsync(visitor);
        var recorded = Read(demo.LedgerFile);

        // Act
        using var ended = await visitor.DeleteAsync("demo/v1/session", Ct);

        // Assert
        var entry = Assert.Single(recorded);
        await Eventually.TrueAsync(() => Read(demo.LedgerFile).Count == 0, "o registro esvaziar depois da limpeza");
        Assert.Equal(6, demo.CareFlow.PurgedServices(entry.Clinic).Count);
    }

    [Fact]
    public async Task ClinicsLeftBeforeARestart_ArePurgedOnStartup()
    {
        // Arrange: um Demo anterior caiu com uma clínica aberta.
        var directory = Directory.CreateTempSubdirectory("careflow-demo-it-").FullName;
        var orphan = Guid.NewGuid();
        new FileClinicLedger(Path.Combine(directory, "estado", "clinicas.json"))
            .Add(new LedgerEntry(orphan, 1, DateTimeOffset.UtcNow.AddMinutes(-20)));

        // Act
        await using var demo = DemoHost.Start(directory);

        // Assert
        await Eventually.TrueAsync(() => demo.CareFlow.PurgedServices(orphan).Count == 6, "a clínica que sobrou ser apagada nos seis serviços");
        await Eventually.TrueAsync(() => Read(demo.LedgerFile).Count == 0, "o registro esvaziar");
    }

    [Fact]
    public async Task OrphanSlot_StaysTakenUntilItsPurgeSucceeds()
    {
        // Arrange: uma partição só, presa por uma clínica que sobrou, com os serviços ainda fora do ar.
        var directory = Directory.CreateTempSubdirectory("careflow-demo-it-").FullName;
        var orphan = Guid.NewGuid();
        new FileClinicLedger(Path.Combine(directory, "estado", "clinicas.json"))
            .Add(new LedgerEntry(orphan, 1, DateTimeOffset.UtcNow.AddMinutes(-20)));
        await using var demo = DemoHost.Start(directory, ("Demo:Slots", "1"));
        demo.CareFlow.PurgeFails = true;
        using var visitor = demo.Visitor();

        // Act
        using var whileStuck = await visitor.PostAsync("demo/v1/sessions", null, Ct);
        demo.CareFlow.PurgeFails = false;
        await Eventually.TrueAsync(() => Read(demo.LedgerFile).Count == 0, "a limpeza da clínica que sobrou dar certo");
        var session = await DemoHost.OpenAsync(visitor);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, whileStuck.StatusCode);
        Assert.Equal("isolated-01", session.Partition);
    }

    [Fact]
    public async Task LedgerThatCannotBeWritten_RefusesTheSession_AndGivesThePartitionBack()
    {
        // Arrange: uma partição só, e o temporário do registro ocupado por uma pasta (gravação falha, como num volume
        // sem permissão).
        await using var demo = DemoHost.Start(("Demo:Slots", "1"));
        var blocker = demo.LedgerFile + ".tmp";
        Directory.CreateDirectory(blocker);
        using var visitor = demo.Visitor();

        // Act
        using var refused = await visitor.PostAsync("demo/v1/sessions", null, Ct);
        Directory.Delete(blocker);
        var session = await DemoHost.OpenAsync(visitor);

        // Assert: nada foi criado nos serviços com o registro fora do ar, e a única partição voltou para o pool.
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, refused.StatusCode);
        Assert.Equal("isolated-01", session.Partition);
        Assert.Single(Read(demo.LedgerFile));
    }

    [Fact]
    public void UnreadableLedger_FailsLoudly_InsteadOfForgettingClinics()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("careflow-demo-it-").FullName;
        var path = Path.Combine(directory, "clinicas.json");
        File.WriteAllText(path, "{ isto não é json");

        // Act
        var act = () => new FileClinicLedger(path);

        // Assert
        Assert.Throws<InvalidOperationException>(act);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void FailedRemove_KeepsTheEntry_SoTheCleanupIsRetried()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("careflow-demo-it-").FullName;
        var path = Path.Combine(directory, "clinicas.json");
        var ledger = new FileClinicLedger(path);
        var clinic = Guid.NewGuid();
        ledger.Add(new LedgerEntry(clinic, 2, DateTimeOffset.UtcNow));
        Directory.CreateDirectory(path + ".tmp");

        // Act
        var act = () => ledger.Remove(clinic);

        // Assert
        Assert.Throws<UnauthorizedAccessException>(act);
        Assert.Equal(clinic, Assert.Single(ledger.Pending()).Clinic);
        Assert.Equal(clinic, Assert.Single(Read(path)).Clinic);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void InMemoryLedger_TracksAddAndRemove()
    {
        // Arrange
        var ledger = new InMemoryClinicLedger();
        var clinic = Guid.NewGuid();

        // Act
        ledger.Add(new LedgerEntry(clinic, 3, DateTimeOffset.UtcNow));
        var afterAdd = ledger.Pending();
        ledger.Remove(clinic);

        // Assert
        Assert.Equal(3, Assert.Single(afterAdd).Slot);
        Assert.Empty(ledger.Pending());
    }

    private static List<LedgerEntry> Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<List<LedgerEntry>>(File.ReadAllText(path), Json) ?? [] : [];
}
