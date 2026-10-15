using System.Diagnostics;

using CareFlow.BuildingBlocks.AspNetCore.Observability;

namespace CareFlow.BuildingBlocks.Tests.Observability;

public sealed class UnparentedQueryFilterTests : IDisposable
{
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name is UnparentedQueryFilter.NpgsqlSource or "CareFlow.Tests",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    private readonly ActivitySource _npgsql = new(UnparentedQueryFilter.NpgsqlSource);
    private readonly ActivitySource _careflow = new("CareFlow.Tests");

    public UnparentedQueryFilterTests() => ActivitySource.AddActivityListener(_listener);

    [Fact]
    public void BackgroundQuery_WithoutParent_IsNotRecorded()
    {
        // Arrange
        using var query = _npgsql.StartActivity("postgresql", ActivityKind.Client)!;

        // Act
        new UnparentedQueryFilter().OnStart(query);

        // Assert
        Assert.False(query.Recorded);
    }

    [Fact]
    public void QueryInsideAnOperation_IsKept()
    {
        // Arrange
        using var operation = _careflow.StartActivity("deliver notification")!;
        using var query = _npgsql.StartActivity("postgresql", ActivityKind.Client)!;

        // Act
        new UnparentedQueryFilter().OnStart(query);

        // Assert
        Assert.True(query.Recorded);
    }

    [Fact]
    public void QueryWithRemoteParent_IsKept()
    {
        // Arrange
        var remote = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        using var query = _npgsql.StartActivity("postgresql", ActivityKind.Client, remote)!;

        // Act
        new UnparentedQueryFilter().OnStart(query);

        // Assert
        Assert.True(query.Recorded);
    }

    [Fact]
    public void RootSpanFromOtherSources_IsKept()
    {
        // Arrange
        using var root = _careflow.StartActivity("deliver notification")!;

        // Act
        new UnparentedQueryFilter().OnStart(root);

        // Assert
        Assert.True(root.Recorded);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _npgsql.Dispose();
        _careflow.Dispose();
    }
}
