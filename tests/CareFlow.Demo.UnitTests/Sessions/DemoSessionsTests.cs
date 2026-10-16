using CareFlow.Demo.Api.Common;
using CareFlow.Demo.Api.Sessions;

using Microsoft.Extensions.Options;

namespace CareFlow.Demo.UnitTests.Sessions;

public sealed class DemoSessionsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static (DemoSessions Sessions, ManualTime Time) Create(int slots = 2, TimeSpan? cooldown = null)
    {
        var time = new ManualTime(Start);
        var options = new DemoOptions
        {
            Slots = slots,
            FirstSlot = 5,
            SessionLifetime = TimeSpan.FromMinutes(15),
            SlotCooldown = cooldown ?? TimeSpan.FromSeconds(60),
        };
        return (new DemoSessions(Options.Create(options), time), time);
    }

    [Fact]
    public void Open_StoresOnlyTheHash_AndTheCookieFindsTheSession()
    {
        // Arrange
        var (sessions, _) = Create();

        // Act
        var (session, cookie) = sessions.Open(Guid.NewGuid(), 5);

        // Assert
        Assert.True(cookie.Length >= 43);
        Assert.DoesNotContain('=', cookie);
        Assert.NotEqual(cookie, session.Id);
        Assert.Equal(64, session.Id.Length);
        Assert.Same(session, sessions.Find(cookie));
        Assert.Equal(1, sessions.Active);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("desconhecido")]
    public void Find_UnknownOrEmptyCookie_ReturnsNull(string? cookie)
    {
        var (sessions, _) = Create();
        sessions.Open(Guid.NewGuid(), 5);

        Assert.Null(sessions.Find(cookie));
    }

    [Fact]
    public void Find_OversizedCookie_IsRejectedWithoutHashing()
    {
        var (sessions, _) = Create();

        Assert.Null(sessions.Find(new string('a', 65)));
    }

    [Fact]
    public void Find_ExpiredSession_ReturnsNull_AndTakeExpiredRemovesIt()
    {
        // Arrange
        var (sessions, time) = Create();
        var (session, cookie) = sessions.Open(Guid.NewGuid(), 5);

        // Act
        time.Advance(TimeSpan.FromMinutes(15));
        var found = sessions.Find(cookie);
        var expired = sessions.TakeExpired();

        // Assert
        Assert.Null(found);
        Assert.Same(session, Assert.Single(expired));
        Assert.Equal(0, sessions.Active);
        Assert.Empty(sessions.TakeExpired());
    }

    [Fact]
    public void Close_InvalidatesTheCookieImmediately()
    {
        var (sessions, _) = Create();
        var (session, cookie) = sessions.Open(Guid.NewGuid(), 5);

        Assert.True(sessions.Close(session));
        Assert.False(sessions.Close(session));
        Assert.Null(sessions.Find(cookie));
    }

    [Fact]
    public void TakeAll_EmptiesTheStore()
    {
        var (sessions, _) = Create();
        sessions.Open(Guid.NewGuid(), 5);
        sessions.Open(Guid.NewGuid(), 6);

        Assert.Equal(2, sessions.TakeAll().Count);
        Assert.Equal(0, sessions.Active);
    }

    [Fact]
    public void ReserveSlot_HandsOutEachSlotOnce_ThenReturnsNull()
    {
        var (sessions, _) = Create(slots: 2);

        Assert.Equal(5, sessions.ReserveSlot());
        Assert.Equal(6, sessions.ReserveSlot());
        Assert.Null(sessions.ReserveSlot());
    }

    [Fact]
    public void ReleaseSlot_WaitsForTheCooldown_BeforeReuse()
    {
        // Arrange
        var (sessions, time) = Create(slots: 1, cooldown: TimeSpan.FromSeconds(60));
        var slot = sessions.ReserveSlot()!.Value;

        // Act
        sessions.ReleaseSlot(slot);
        var duringCooldown = sessions.ReserveSlot();
        time.Advance(TimeSpan.FromSeconds(60));
        var afterCooldown = sessions.ReserveSlot();

        // Assert
        Assert.Null(duringCooldown);
        Assert.Equal(slot, afterCooldown);
    }

    [Fact]
    public void ReleaseSlot_WithExplicitCooldown_OverridesTheDefault()
    {
        var (sessions, _) = Create(slots: 1);
        var slot = sessions.ReserveSlot()!.Value;

        sessions.ReleaseSlot(slot, TimeSpan.Zero);

        Assert.Equal(slot, sessions.ReserveSlot());
    }
}

public sealed class DemoSessionTests
{
    [Fact]
    public void TryReserveBooking_CountsUpToTheLimit()
    {
        // Arrange
        var session = new DemoSession("id", Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue);
        var indexes = new List<int>();

        // Act
        for (var i = 0; i < 3; i++)
        {
            if (session.TryReserveBooking(2, out var index))
            {
                indexes.Add(index);
            }
        }

        // Assert
        Assert.Equal([0, 1], indexes);
        Assert.False(session.TryReserveBooking(2, out var last));
        Assert.Equal(2, last);
    }

    [Fact]
    public void Traces_AreOwnedOnlyAfterBeingAdded()
    {
        var session = new DemoSession("id", Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue);
        var trace = new string('a', 32);

        Assert.False(session.OwnsTrace(trace));
        session.AddTrace(trace);
        session.AddTrace(trace);

        Assert.True(session.OwnsTrace(trace));
        Assert.Equal([trace], session.Traces);
    }

    [Fact]
    public void Appointments_ReturnsACopy()
    {
        var session = new DemoSession("id", Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue);
        var appointment = Guid.NewGuid();
        session.AddAppointment(appointment);

        var snapshot = session.Appointments;
        session.AddAppointment(Guid.NewGuid());

        Assert.Equal([appointment], snapshot);
        Assert.Equal(2, session.Appointments.Count);
    }
    [Fact]
    public void RightsRequests_CountUpToTheLimit_AndAreOwnedOnlyAfterBeingAdded()
    {
        // Arrange
        var session = new DemoSession("id", Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue);
        var request = Guid.NewGuid();

        // Act
        var first = session.TryReserveRightsRequest(2);
        var second = session.TryReserveRightsRequest(2);
        var third = session.TryReserveRightsRequest(2);
        var ownedBefore = session.OwnsRightsRequest(request);
        session.AddRightsRequest(request);

        // Assert
        Assert.True(first);
        Assert.True(second);
        Assert.False(third);
        Assert.False(ownedBefore);
        Assert.True(session.OwnsRightsRequest(request));
        Assert.False(session.OwnsRightsRequest(Guid.NewGuid()));
    }
}
