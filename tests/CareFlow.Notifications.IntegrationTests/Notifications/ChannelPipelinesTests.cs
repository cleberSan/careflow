using System.Net;

using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Domain.Channels;

using Polly;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>
/// O registro de pipelines pode montar o pipeline de uma partição duas vezes quando as primeiras chamadas chegam
/// juntas. A segunda montagem precisa funcionar e o estado do circuito continua vindo das transições.
/// </summary>
public sealed class ChannelPipelinesTests
{
    private static readonly ChannelResilienceOptions Options = new()
    {
        MaxRetryAttempts = 1,
        RetryBaseDelay = TimeSpan.Zero,
        MinimumThroughput = 2,
        FailureRatio = 0.5,
        SamplingDuration = TimeSpan.FromSeconds(30),
        BreakDuration = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public void BuildingTheSamePartitionTwice_DoesNotThrow()
    {
        // Arrange
        var circuits = new ChannelCircuits();

        // Act
        var first = Build(circuits, "isolated-01");
        var second = Build(circuits, "isolated-01");

        // Assert
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Failures_OpenOnlyThatPartition_AndManualIsolationIsReported()
    {
        // Arrange
        var circuits = new ChannelCircuits();
        var pipeline = Build(circuits, "isolated-02");
        var other = Build(circuits, "isolated-03");
        var transitions = new List<(string Partition, ChannelCircuitState State)>();
        circuits.PartitionStateChanged += (_, partition, state) => transitions.Add((partition, state));

        // Act
        // Duas tentativas com 503 abrem o circuito (vazão mínima 2, metade de falhas).
        using var response = await pipeline.ExecuteAsync(
            _ => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), TestContext.Current.CancellationToken);

        var opened = circuits.StateOf(Channel.WhatsApp, "isolated-02");
        await circuits.ControlFor(Channel.WhatsApp, "isolated-03").IsolateAsync(TestContext.Current.CancellationToken);
        var isolated = circuits.StateOf(Channel.WhatsApp, "isolated-03");
        await circuits.ControlFor(Channel.WhatsApp, "isolated-03").CloseAsync(TestContext.Current.CancellationToken);
        GC.KeepAlive(other);

        // Assert
        Assert.Equal(ChannelCircuitState.Open, opened);
        Assert.Equal(ChannelCircuitState.Isolated, isolated);
        Assert.Equal(ChannelCircuitState.Closed, circuits.StateOf(Channel.WhatsApp, "isolated-03"));
        Assert.Equal(ChannelCircuitState.Closed, circuits.StateOf(Channel.WhatsApp));
        Assert.Contains(circuits.SnapshotAll(), s => s.Partition == "isolated-02" && s.State == ChannelCircuitState.Open);
        Assert.Equal(
            [("isolated-02", ChannelCircuitState.Open), ("isolated-03", ChannelCircuitState.Isolated), ("isolated-03", ChannelCircuitState.Closed)],
            transitions);
    }

    private static ResiliencePipeline<HttpResponseMessage> Build(ChannelCircuits circuits, string partition)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        ChannelPipelines.ConfigureSend(builder, Channel.WhatsApp, Options, circuits, partition, () => new FaultInjectionOptions());
        return builder.Build();
    }
}
