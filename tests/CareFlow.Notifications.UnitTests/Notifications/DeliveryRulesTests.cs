using CareFlow.Notifications.Domain.Appointments;
using CareFlow.Notifications.Domain.Consents;
using CareFlow.Notifications.Domain.Notifications;

using static CareFlow.Notifications.UnitTests.Notifications.Fixtures;

namespace CareFlow.Notifications.UnitTests.Notifications;

public sealed class DeliveryRulesTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static NotificationConsent Consent(Notification notification, Func<NotificationConsent, bool> apply)
    {
        var consent = NotificationConsent.Track(notification.TenantId, notification.PatientRef, Now);
        apply(consent);
        return consent;
    }

    private static AppointmentTrack Appointment(Notification notification, AppointmentState state)
    {
        var track = AppointmentTrack.Track(notification.TenantId, notification.AppointmentId, notification.PatientRef, notification.AppointmentStartsAt, Now);
        if (state == AppointmentState.Booked)
        {
            track.ApplyBooked(Now);
        }
        else if (state == AppointmentState.Cancelled)
        {
            track.ApplyCancelled(Now);
        }

        return track;
    }

    [Fact]
    public void Granted_Sends()
    {
        var notification = Confirmation();
        var decision = DeliveryRules.Evaluate(notification, Appointment(notification, AppointmentState.Booked), Consent(notification, c => c.ApplyGranted(1, Now)), Grace, Now);
        Assert.Equal(DeliveryDecision.Send, decision);
    }

    [Fact]
    public void Revoked_Blocks()
    {
        var notification = Confirmation();
        var consent = Consent(notification, c => c.ApplyGranted(1, Now) && c.ApplyRevoked(2, Now));
        Assert.Equal(DeliveryDecision.BlockNoConsent, DeliveryRules.Evaluate(notification, null, consent, Grace, Now));
    }

    [Fact]
    public void UnknownConsent_WaitsDuringGraceThenBlocks()
    {
        var notification = Confirmation();
        Assert.Equal(DeliveryDecision.WaitForConsent, DeliveryRules.Evaluate(notification, null, null, Grace, Now.AddMinutes(4)));
        Assert.Equal(DeliveryDecision.BlockNoConsent, DeliveryRules.Evaluate(notification, null, null, Grace, Now.AddMinutes(5)));
        Assert.Equal(DeliveryDecision.WaitForConsent, DeliveryRules.Evaluate(notification, null, NotificationConsent.Track(Guid.NewGuid(), Guid.NewGuid(), Now), Grace, Now));
    }

    [Fact]
    public void CancelledAppointment_SupersedesConfirmationButNotCancellation()
    {
        var confirmation = Confirmation();
        var cancellation = Cancellation();
        var granted = Consent(confirmation, c => c.ApplyGranted(1, Now));

        Assert.Equal(DeliveryDecision.Supersede, DeliveryRules.Evaluate(confirmation, Appointment(confirmation, AppointmentState.Cancelled), granted, Grace, Now));
        Assert.Equal(DeliveryDecision.Send, DeliveryRules.Evaluate(cancellation, Appointment(cancellation, AppointmentState.Cancelled), granted, Grace, Now));
    }

    [Fact]
    public void StartedAppointment_Expires()
    {
        var notification = Confirmation();
        Assert.Equal(DeliveryDecision.Expire, DeliveryRules.Evaluate(notification, null, null, Grace, StartsAt));
    }

    [Fact]
    public void ClosedNotification_IsAlreadyClosed()
    {
        var notification = Confirmation();
        notification.MarkFailed(Now);
        Assert.Equal(DeliveryDecision.AlreadyClosed, DeliveryRules.Evaluate(notification, null, null, Grace, Now));
        Assert.Throws<ArgumentNullException>(() => DeliveryRules.Evaluate(null!, null, null, Grace, Now));
    }
}
