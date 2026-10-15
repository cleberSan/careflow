using CareFlow.Contracts.Consents;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>Sem consentimento para notificações, o aviso não sai: fica registrado como não enviado e auditado.</summary>
public sealed class ConsentTests(NotificationsApiFixture api) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await api.ResetChannelsAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task RevokedConsent_BlocksSend_AuditsReason_AndNeverReadsContact()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();

        // Act
        await api.DeliverAsync(scenario.Tenant, new ConsentRevoked(scenario.Patient, ConsentPurposes.Notifications, 2, DateTimeOffset.UtcNow));
        await api.DispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");
        Assert.Equal(("NotSent", "NoConsent"), (confirmation.Status, confirmation.NotSentReason));
        Assert.Empty(confirmation.Attempts);
        Assert.Contains("notification.confirmation.not-sent.no-consent", await scenario.AuditActionsAsync());
        Assert.Equal(0, api.Contacts.ReadsOf(scenario.Tenant, scenario.Patient));
        Assert.DoesNotContain(api.Ledger.Messages, m => m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Phone!));
    }

    [Fact]
    public async Task OlderGrantArrivingLate_DoesNotUndoRevocation()
    {
        // Arrange
        var scenario = new Scenario(api);
        await scenario.WithContactAsync(scenario.Contact);
        var booked = await scenario.BookAsync();

        // Act: revogação (versão 2) chega antes da concessão (versão 1).
        await api.DeliverAsync(scenario.Tenant, new ConsentRevoked(scenario.Patient, ConsentPurposes.Notifications, 2, DateTimeOffset.UtcNow));
        await api.GrantNotificationsAsync(scenario.Tenant, scenario.Patient, version: 1);
        await api.DispatchAsync();

        // Assert
        Assert.Equal("NotSent", (await scenario.NotificationAsync(booked.AppointmentId, "Confirmation")).Status);
    }

    [Fact]
    public async Task RevocationOfAnotherPurpose_IsIgnored()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();

        // Act
        await api.DeliverAsync(scenario.Tenant, new ConsentRevoked(scenario.Patient, ConsentPurposes.Scheduling, 5, DateTimeOffset.UtcNow));
        await api.DeliverAsync(scenario.Tenant, new ConsentGranted(scenario.Patient, ConsentPurposes.AiTriage, "consent", "v1", 6, DateTimeOffset.UtcNow));
        await api.DispatchAsync();

        // Assert
        Assert.Equal("Sent", (await scenario.NotificationAsync(booked.AppointmentId, "Confirmation")).Status);
    }

    [Fact]
    public async Task UnknownConsent_WaitsDuringGracePeriod_ThenSendsWhenGrantArrives()
    {
        // Arrange
        var scenario = new Scenario(api);
        await scenario.WithContactAsync(scenario.Contact);
        var booked = await scenario.BookAsync();

        // Act 1: sem notícia do Consent, o aviso espera.
        await api.DispatchAsync();
        var waiting = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");

        // Act 2: a concessão chega e o aviso volta a vencer.
        await api.GrantNotificationsAsync(scenario.Tenant, scenario.Patient);
        await api.ExecuteAsAdminAsync("UPDATE notification_jobs SET due_at = now() WHERE tenant_id = $1 AND due_at < now() + interval '1 hour'", scenario.Tenant);
        await api.DispatchAsync();

        // Assert
        Assert.Equal("Pending", waiting.Status);
        Assert.Equal("Sent", (await scenario.NotificationAsync(booked.AppointmentId, "Confirmation")).Status);
    }

    [Fact]
    public async Task UnknownConsent_AfterGracePeriod_IsNotSent()
    {
        // Arrange
        var scenario = new Scenario(api);
        await scenario.WithContactAsync(scenario.Contact);
        var booked = await scenario.BookAsync();

        // Act: o aviso foi criado há mais tempo que a carência e o consentimento nunca chegou.
        await api.ExecuteAsAdminAsync(
            "UPDATE notifications SET created_at = now() - interval '10 minutes' WHERE tenant_id = $1", scenario.Tenant);
        await api.DispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");
        Assert.Equal(("NotSent", "NoConsent"), (confirmation.Status, confirmation.NotSentReason));
    }
}
