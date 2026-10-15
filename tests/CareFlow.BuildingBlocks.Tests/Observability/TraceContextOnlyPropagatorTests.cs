using System.Diagnostics;

using CareFlow.BuildingBlocks.AspNetCore.Observability;

using Microsoft.Extensions.DependencyInjection;

namespace CareFlow.BuildingBlocks.Tests.Observability;

public sealed class TraceContextOnlyPropagatorTests
{
    private const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    [Fact]
    public void Inject_WritesTraceContext_ButNeverBaggage()
    {
        // Arrange
        using var activity = new Activity("teste").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "vendor=1";
        activity.AddBaggage("cpf", "12345678909");
        var headers = new Dictionary<string, string>();

        // Act
        TraceContextOnlyPropagator.Instance.Inject(activity, headers, (carrier, name, value) => ((Dictionary<string, string>)carrier!)[name] = value);

        // Assert
        Assert.Equal(["traceparent", "tracestate"], headers.Keys.Order());
        Assert.Equal(activity.Id, headers["traceparent"]);
        Assert.Equal(["traceparent", "tracestate"], TraceContextOnlyPropagator.Instance.Fields);
    }

    [Fact]
    public void Inject_WithoutW3CActivity_WritesNothing()
    {
        // Arrange
        using var activity = new Activity("teste").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        var headers = new Dictionary<string, string>();

        // Act
        TraceContextOnlyPropagator.Instance.Inject(activity, headers, (carrier, name, value) => ((Dictionary<string, string>)carrier!)[name] = value);
        TraceContextOnlyPropagator.Instance.Inject(null, headers, (carrier, name, value) => ((Dictionary<string, string>)carrier!)[name] = value);

        // Assert
        Assert.Empty(headers);
    }

    [Fact]
    public void Extract_ReadsTraceContext_AndDropsBaggage()
    {
        // Arrange
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = TraceParent,
            ["tracestate"] = "vendor=1",
            ["baggage"] = "cpf=12345678909",
        };
        static void Getter(object? carrier, string name, out string? value, out IEnumerable<string>? values)
        {
            values = null;
            value = ((Dictionary<string, string>)carrier!).GetValueOrDefault(name);
        }

        // Act
        TraceContextOnlyPropagator.Instance.ExtractTraceIdAndState(headers, Getter, out var traceId, out var traceState);
        var baggage = TraceContextOnlyPropagator.Instance.ExtractBaggage(headers, Getter);

        // Assert
        Assert.Equal(TraceParent, traceId);
        Assert.Equal("vendor=1", traceState);
        Assert.Null(baggage);
    }

    [Fact]
    public void Install_ReplacesTheGlobalAndTheRegisteredPropagator()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(DistributedContextPropagator.CreateDefaultPropagator());

        // Act
        TraceContextOnlyPropagator.Install(services);

        // Assert
        Assert.Same(TraceContextOnlyPropagator.Instance, DistributedContextPropagator.Current);
        Assert.Same(TraceContextOnlyPropagator.Instance, services.BuildServiceProvider().GetRequiredService<DistributedContextPropagator>());
    }
}
