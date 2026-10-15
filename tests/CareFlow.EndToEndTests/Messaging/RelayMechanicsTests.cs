using CareFlow.BuildingBlocks.Messaging.Outbox;

namespace CareFlow.EndToEndTests.Messaging;

/// <summary>Peças do despachante que não dependem de broker nem banco.</summary>
public sealed class RelayMechanicsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, 1600, 2400)]
    [InlineData(3, 6400, 9600)]
    public void Backoff_GrowsExponentiallyWithJitter(int failures, double minimumMs, double maximumMs)
    {
        // Act
        var delay = MessageRelay.Backoff(failures, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));

        // Assert
        Assert.InRange(delay.TotalMilliseconds, minimumMs, maximumMs);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1000)]
    public void Backoff_NeverExceedsMaximum(int failures)
    {
        // Act
        var delay = MessageRelay.Backoff(failures, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        // Assert
        Assert.True(delay <= TimeSpan.FromSeconds(30));
        Assert.True(delay >= TimeSpan.FromSeconds(24));
    }

    [Fact]
    public void MonotonicGuid_SameMillisecond_IsStrictlyIncreasing()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;

        // Act
        var ids = Enumerable.Range(0, 1000).Select(_ => MonotonicGuid.Next(now)).ToArray();

        // Assert
        for (var i = 1; i < ids.Length; i++)
        {
            Assert.True(ids[i].CompareTo(ids[i - 1]) > 0, $"id {i} não é maior que o anterior");
        }

        Assert.Equal(7, ids[0].Version);
    }

    [Fact]
    public void MonotonicGuid_ClockGoingBackwards_StillIncreases()
    {
        // Arrange
        var first = MonotonicGuid.Next(DateTimeOffset.UtcNow.AddMinutes(1));

        // Act
        var second = MonotonicGuid.Next(DateTimeOffset.UtcNow.AddMinutes(-1));

        // Assert
        Assert.True(second.CompareTo(first) > 0);
    }

    [Fact]
    public async Task OutboxSignal_NotifyWakesWaiter()
    {
        // Arrange
        using var signal = new OutboxSignal();
        signal.Notify();

        // Act
        var woke = await signal.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Assert
        Assert.True(woke);
    }

    [Fact]
    public async Task OutboxSignal_ManyNotifies_WakeOnlyOnce()
    {
        // Arrange
        using var signal = new OutboxSignal();
        for (var i = 0; i < 10; i++)
        {
            signal.Notify();
        }

        // Act
        var first = await signal.WaitAsync(TimeSpan.FromMilliseconds(100), Ct);
        var second = await signal.WaitAsync(TimeSpan.FromMilliseconds(50), Ct);

        // Assert
        Assert.True(first);
        Assert.False(second);
    }
}
