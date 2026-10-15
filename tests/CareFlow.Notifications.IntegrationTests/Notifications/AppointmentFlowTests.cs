using System.Net.Http.Json;

using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts.Appointments;
using CareFlow.Contracts.Consents;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>Da reserva ao aviso: confirmação, lembrete e cancelamento, com eventos repetidos e fora de ordem.</summary>
public sealed class AppointmentFlowTests(NotificationsApiFixture api) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await api.ResetChannelsAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Booked_SendsConfirmationByPreferredChannel_AndSchedulesReminder()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();

        // Act
        await api.DispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");
        Assert.Equal("Sent", confirmation.Status);
        Assert.Equal("WhatsApp", confirmation.SentVia);
        var attempt = Assert.Single(confirmation.Attempts);
        Assert.Equal(("WhatsApp", "Delivered"), (attempt.Channel, attempt.Outcome));

        var reminder = await scenario.NotificationAsync(booked.AppointmentId, "Reminder");
        Assert.Equal("Pending", reminder.Status);
        Assert.Equal(booked.StartsAt.AddHours(-24), reminder.DueAt);

        var message = Assert.Single(api.Ledger.Messages, m => m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Phone!));
        Assert.StartsWith("CareFlow: sua consulta está marcada para ", message.Text, StringComparison.Ordinal);
        Assert.EndsWith(" em sua clínica.", message.Text, StringComparison.Ordinal);
        Assert.Contains("notification.confirmation.sent.whatsapp", await scenario.AuditActionsAsync());
    }

    [Fact]
    public async Task Reminder_IsSentWhenItsTimeComes()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();
        await api.DispatchAsync();

        // Act: o relógio "chega" na antecedência do lembrete.
        await api.ExecuteAsAdminAsync(
            "UPDATE notification_jobs SET due_at = now() WHERE tenant_id = $1", scenario.Tenant);
        await api.DispatchAsync();

        // Assert
        var reminder = await scenario.NotificationAsync(booked.AppointmentId, "Reminder");
        Assert.Equal("Sent", reminder.Status);
        Assert.Contains(api.Ledger.Messages, m => m.Text.StartsWith("CareFlow: lembrete da sua consulta", StringComparison.Ordinal)
            && m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Phone!));
        Assert.Contains("notification.reminder.sent.whatsapp", await scenario.AuditActionsAsync());
    }

    [Fact]
    public async Task Booked_WithinReminderLead_SchedulesOnlyConfirmation()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);

        // Act
        var booked = await scenario.BookAsync(DateTimeOffset.UtcNow.AddHours(3));

        // Assert
        var only = Assert.Single(await scenario.NotificationsAsync(booked.AppointmentId));
        Assert.Equal("Confirmation", only.Kind);
    }

    [Fact]
    public async Task Booked_ForPastAppointment_SchedulesNothing()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);

        // Act: evento muito atrasado, de uma consulta que já começou.
        var booked = await scenario.BookAsync(DateTimeOffset.UtcNow.AddHours(-1));

        // Assert
        Assert.Empty(await scenario.NotificationsAsync(booked.AppointmentId));
    }

    [Fact]
    public async Task ClinicSettings_DriveChannelOrderNameAndTimeZone()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var admin = api.CreateClient(scenario.Tenant, Admin);
        var put = await admin.PutAsJsonAsync(
            "/v1/settings", new { displayName = "Clinica Aurora", timeZone = "America/Manaus", channelOrder = Channels("Email", "Sms"), reminderLeadMinutes = 120 }, Ct);
        put.EnsureSuccessStatusCode();
        var startsAt = new DateTimeOffset(DateTimeOffset.UtcNow.Year + 1, 3, 10, 14, 0, 0, TimeSpan.Zero);

        // Act
        var booked = await scenario.BookAsync(startsAt);
        await api.DispatchAsync();

        // Assert: Manaus é UTC-4, então 14:00 UTC vira 10:00.
        var confirmation = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");
        Assert.Equal("Email", confirmation.SentVia);
        Assert.Equal(startsAt.AddHours(-2), (await scenario.NotificationAsync(booked.AppointmentId, "Reminder")).DueAt);
        var message = Assert.Single(api.Ledger.Messages, m => m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Email!));
        Assert.Contains($"10/03/{startsAt.Year} às 10:00 em Clinica Aurora", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateEvent_SameMessage_IsDiscardedByInbox()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = Booked(scenario.Patient);
        var messageId = Guid.CreateVersion7();

        // Act
        var first = await api.DeliverAsync(scenario.Tenant, booked, messageId);
        var second = await api.DeliverAsync(scenario.Tenant, booked, messageId);

        // Assert
        Assert.Equal(ProcessOutcome.Processed, first);
        Assert.Equal(ProcessOutcome.Duplicate, second);
        Assert.Equal(2, (await scenario.NotificationsAsync(booked.AppointmentId)).Count);
    }

    [Fact]
    public async Task DuplicateEvent_RepublishedWithNewId_DoesNotNotifyTwice()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = Booked(scenario.Patient);

        // Act
        await api.DeliverAsync(scenario.Tenant, booked);
        await api.DeliverAsync(scenario.Tenant, booked);
        await api.DispatchAsync();

        // Assert
        var notifications = await scenario.NotificationsAsync(booked.AppointmentId);
        Assert.Single(notifications, n => n.Kind == "Confirmation");
        Assert.Single(notifications, n => n.Kind == "Reminder");
        Assert.Single(api.Ledger.Messages, m => m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Phone!));
    }

    [Fact]
    public async Task CancellationBeforeBooking_OutOfOrder_SendsOnlyTheCancellation()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = Booked(scenario.Patient);

        // Act: o cancelamento chega primeiro; a reserva atrasada vem depois.
        await api.DeliverAsync(scenario.Tenant, Cancelled(booked));
        await api.DeliverAsync(scenario.Tenant, booked);
        await api.DispatchAsync();

        // Assert
        var only = Assert.Single(await scenario.NotificationsAsync(booked.AppointmentId));
        Assert.Equal(("Cancellation", "Sent"), (only.Kind, only.Status));
        var message = Assert.Single(api.Ledger.Messages, m => m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Phone!));
        Assert.Contains("foi cancelada", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeConfirmationIsSent_SupersedesConfirmationAndReminder()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();

        // Act: cancelada antes de o despachante rodar.
        await api.DeliverAsync(scenario.Tenant, Cancelled(booked));
        await api.DispatchAsync();

        // Assert
        var notifications = await scenario.NotificationsAsync(booked.AppointmentId);
        Assert.Equal("Superseded", notifications.Single(n => n.Kind == "Confirmation").Status);
        Assert.Equal("Superseded", notifications.Single(n => n.Kind == "Reminder").Status);
        Assert.Equal("Sent", notifications.Single(n => n.Kind == "Cancellation").Status);
        var message = Assert.Single(api.Ledger.Messages, m => m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Phone!));
        Assert.Contains("foi cancelada", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationByConsentRevocation_AfterConfirmation_ExplainsTheReason()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();
        await api.DispatchAsync();

        // Act
        await api.DeliverAsync(scenario.Tenant, Cancelled(booked, AppointmentCancellationReasons.ConsentRevoked));
        await api.DeliverAsync(scenario.Tenant, Cancelled(booked, AppointmentCancellationReasons.ConsentRevoked));
        await api.DispatchAsync();

        // Assert
        var notifications = await scenario.NotificationsAsync(booked.AppointmentId);
        Assert.Equal("Sent", notifications.Single(n => n.Kind == "Confirmation").Status);
        Assert.Equal("Superseded", notifications.Single(n => n.Kind == "Reminder").Status);
        Assert.Equal("Sent", notifications.Single(n => n.Kind == "Cancellation").Status);
        var texts = api.Ledger.Messages.Where(m => m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Phone!)).Select(m => m.Text).ToArray();
        Assert.Equal(2, texts.Length);
        Assert.Contains(texts, t => t.Contains("consentimento para agendamento foi revogado", StringComparison.Ordinal));
        Assert.Contains("notification.cancellation.sent.whatsapp", await scenario.AuditActionsAsync());
    }

    [Fact]
    public async Task AppointmentAlreadyStarted_WhenTheJobRuns_ExpiresWithoutSending()
    {
        // Arrange: a fila ficou parada até depois do horário da consulta.
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();
        await api.ExecuteAsAdminAsync(
            "UPDATE notifications SET appointment_starts_at = now() - interval '1 hour' WHERE appointment_id = $1", booked.AppointmentId);

        // Act
        await api.DispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");
        Assert.Equal("Expired", confirmation.Status);
        Assert.Empty(confirmation.Attempts);
        Assert.DoesNotContain(api.Ledger.Messages, m => m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Phone!));
    }

    [Fact]
    public async Task CancellationRecordedWhileJobWasClaimed_SupersedesAtSendTime()
    {
        // Arrange: a corrida em que o cancelamento chega depois de o job ser pego e antes do envio. As regras são
        // reavaliadas na hora do envio, então o estado da consulta decide, não o job.
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();
        await api.ExecuteAsAdminAsync("UPDATE appointment_tracks SET state = 'Cancelled' WHERE appointment_id = $1", booked.AppointmentId);

        // Act
        await api.DispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");
        Assert.Equal("Superseded", confirmation.Status);
        Assert.Empty(confirmation.Attempts);
    }

    [Fact]
    public async Task InvalidEvents_AreRejectedAsPoison()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = Booked(scenario.Patient);

        // Act
        var unknownReason = await api.DeliverAsync(scenario.Tenant, Cancelled(booked, "porque-sim"));
        var emptyPatient = await api.DeliverAsync(scenario.Tenant, booked with { PatientRef = Guid.Empty });
        var emptyAppointment = await api.DeliverAsync(scenario.Tenant, booked with { AppointmentId = Guid.Empty });
        var inverted = await api.DeliverAsync(scenario.Tenant, booked with { EndsAt = booked.StartsAt });
        var zeroVersion = await api.DeliverAsync(scenario.Tenant, new ConsentGranted(scenario.Patient, ConsentPurposes.Notifications, "consent", "v1", 0, DateTimeOffset.UtcNow));
        var emptyConsentPatient = await api.DeliverAsync(scenario.Tenant, new ConsentRevoked(Guid.Empty, ConsentPurposes.Notifications, 3, DateTimeOffset.UtcNow));

        // Assert
        Assert.All([unknownReason, emptyPatient, emptyAppointment, inverted, zeroVersion, emptyConsentPatient], outcome => Assert.Equal(ProcessOutcome.Invalid, outcome));
        Assert.Empty(await scenario.NotificationsAsync(booked.AppointmentId));
    }
}
