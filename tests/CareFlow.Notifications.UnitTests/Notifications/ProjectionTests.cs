using CareFlow.Notifications.Domain.Appointments;
using CareFlow.Notifications.Domain.Consents;

using static CareFlow.Notifications.UnitTests.Notifications.Fixtures;

namespace CareFlow.Notifications.UnitTests.Notifications;

public sealed class ProjectionTests
{
    [Fact]
    public void Appointment_BookedOnce()
    {
        var track = AppointmentTrack.Track(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StartsAt, Now);

        Assert.True(track.ApplyBooked(Now));
        Assert.False(track.ApplyBooked(Now));
        Assert.Equal(AppointmentState.Booked, track.State);
    }

    [Fact]
    public void Appointment_CancelledBeforeBooked_IgnoresLateBooking()
    {
        var track = AppointmentTrack.Track(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StartsAt, Now);

        Assert.True(track.ApplyCancelled(Now));
        Assert.False(track.ApplyBooked(Now));
        Assert.False(track.ApplyCancelled(Now));
        Assert.Equal(AppointmentState.Cancelled, track.State);
    }

    [Fact]
    public void Appointment_RequiresIdentifiersAndStoresUtc()
    {
        Assert.Throws<ArgumentException>(() => AppointmentTrack.Track(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), StartsAt, Now));
        Assert.Throws<ArgumentException>(() => AppointmentTrack.Track(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), StartsAt, Now));
        Assert.Throws<ArgumentException>(() => AppointmentTrack.Track(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, StartsAt, Now));

        var track = AppointmentTrack.Track(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StartsAt.ToOffset(TimeSpan.FromHours(-3)), Now);
        Assert.Equal(TimeSpan.Zero, track.StartsAt.Offset);
    }

    [Fact]
    public void Consent_AppliesOnlyNewerVersions()
    {
        var consent = NotificationConsent.Track(Guid.NewGuid(), Guid.NewGuid(), Now);

        Assert.True(consent.ApplyGranted(1, Now));
        Assert.True(consent.ApplyRevoked(3, Now));
        Assert.False(consent.ApplyGranted(2, Now));
        Assert.False(consent.ApplyRevoked(3, Now));
        Assert.Equal(NotificationConsentState.Revoked, consent.State);
        Assert.Equal(3, consent.Version);
    }

    [Fact]
    public void Consent_RequiresIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => NotificationConsent.Track(Guid.Empty, Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => NotificationConsent.Track(Guid.NewGuid(), Guid.Empty, Now));
    }
}
