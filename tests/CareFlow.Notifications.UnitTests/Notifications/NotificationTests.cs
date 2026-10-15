using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Notifications;

using static CareFlow.Notifications.UnitTests.Notifications.Fixtures;

namespace CareFlow.Notifications.UnitTests.Notifications;

public sealed class NotificationTests
{
    [Fact]
    public void Confirmation_IsPendingAndDueNow()
    {
        // Act
        var notification = Confirmation();

        // Assert
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(NotificationKind.Confirmation, notification.Kind);
        Assert.Equal(Now, notification.DueAt);
        Assert.Equal(0, notification.Rounds);
        Assert.Null(notification.CancellationCause);
    }

    [Fact]
    public void Reminder_IsDueLeadBeforeStart()
    {
        // Act
        var reminder = Notification.Reminder(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StartsAt, TimeSpan.FromHours(24), Now);

        // Assert
        Assert.NotNull(reminder);
        Assert.Equal(StartsAt.AddHours(-24), reminder.DueAt);
        Assert.Equal(NotificationKind.Reminder, reminder.Kind);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(23)]
    public void Reminder_WhenLeadAlreadyPassed_IsNotCreated(int hoursUntilStart)
    {
        // Act: a consulta começa em menos tempo (ou exatamente) que a antecedência.
        var reminder = Notification.Reminder(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(hoursUntilStart), TimeSpan.FromHours(24), Now);

        // Assert
        Assert.Null(reminder);
    }

    [Fact]
    public void Create_WithEmptyIdentifiers_Throws()
    {
        Assert.Throws<ArgumentException>(() => Notification.Confirmation(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), StartsAt, Now));
        Assert.Throws<ArgumentException>(() => Notification.Confirmation(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), StartsAt, Now));
        Assert.Throws<ArgumentException>(() => Notification.Confirmation(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, StartsAt, Now));
    }

    [Fact]
    public void IdempotencyKey_IsStablePerChannelAndDistinctAcrossChannels()
    {
        // Arrange
        var notification = Confirmation();

        // Act
        var whatsapp = notification.IdempotencyKeyFor(Channel.WhatsApp);

        // Assert
        Assert.Equal(whatsapp, notification.IdempotencyKeyFor(Channel.WhatsApp));
        Assert.NotEqual(whatsapp, notification.IdempotencyKeyFor(Channel.Sms));
        Assert.Equal($"{notification.Id:N}-sms", notification.IdempotencyKeyFor(Channel.Sms));
        Assert.True(whatsapp.Length <= 64);
    }

    [Fact]
    public void MarkSent_RecordsChannelAndProvider()
    {
        // Arrange
        var notification = Confirmation();

        // Act
        var sent = notification.MarkSent(Channel.Sms, "sim-1", Now);

        // Assert
        Assert.True(sent);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Equal(Channel.Sms, notification.SentVia);
        Assert.Equal("sim-1", notification.ProviderMessageId);
        Assert.Equal(Now, notification.CompletedAt);
    }

    [Fact]
    public void MarkSent_AfterSupersede_StillRecordsTheSend()
    {
        // Arrange: o cancelamento chegou enquanto a mensagem saía.
        var notification = Confirmation();
        notification.Supersede(Now);

        // Act
        var sent = notification.MarkSent(Channel.WhatsApp, "sim-2", Now);

        // Assert
        Assert.True(sent);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
    }

    [Fact]
    public void MarkSent_OnClosedNotification_IsIgnored()
    {
        // Arrange
        var notification = Confirmation();
        notification.MarkNotSent(NotSentReason.NoConsent, Now);

        // Act
        var sent = notification.MarkSent(Channel.WhatsApp, "sim-3", Now);

        // Assert
        Assert.False(sent);
        Assert.Equal(NotificationStatus.NotSent, notification.Status);
        Assert.Equal(NotSentReason.NoConsent, notification.NotSentReason);
    }

    [Fact]
    public void Close_OnlyOnce()
    {
        // Arrange
        var notification = Confirmation();

        // Act
        var first = notification.Expire(Now);
        var second = notification.MarkFailed(Now);
        var third = notification.Supersede(Now);
        var fourth = notification.MarkNotSent(NotSentReason.NoContact, Now);

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.False(third);
        Assert.False(fourth);
        Assert.Equal(NotificationStatus.Expired, notification.Status);
        Assert.Null(notification.NotSentReason);
    }

    [Fact]
    public void RegisterFailedRound_CountsRoundsAndRequiresPending()
    {
        // Arrange
        var notification = Confirmation();

        // Act
        notification.RegisterFailedRound();
        var rounds = notification.RegisterFailedRound();
        notification.MarkFailed(Now);

        // Assert
        Assert.Equal(2, rounds);
        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Throws<InvalidOperationException>(() => notification.RegisterFailedRound());
    }

    [Fact]
    public void MarkSent_RequiresProviderId()
    {
        Assert.Throws<ArgumentException>(() => Confirmation().MarkSent(Channel.Email, " ", Now));
    }

    [Fact]
    public void DeliveryAttempt_TruncatesErrorAndCopiesKey()
    {
        // Arrange
        var notification = Confirmation();

        // Act
        var attempt = DeliveryAttempt.Record(notification, 2, Channel.Email, AttemptOutcome.Failed, new string('x', 100), Now);
        var clean = DeliveryAttempt.Record(notification, 1, Channel.Sms, AttemptOutcome.Delivered, null, Now);

        // Assert
        Assert.Equal(DeliveryAttempt.MaxErrorLength, attempt.Error!.Length);
        Assert.Equal(notification.IdempotencyKeyFor(Channel.Email), attempt.IdempotencyKey);
        Assert.Equal(notification.Id, attempt.NotificationId);
        Assert.Equal(notification.TenantId, attempt.TenantId);
        Assert.Equal(2, attempt.Round);
        Assert.Null(clean.Error);
        Assert.Throws<ArgumentNullException>(() => DeliveryAttempt.Record(null!, 1, Channel.Sms, AttemptOutcome.Failed, null, Now));
    }
}
