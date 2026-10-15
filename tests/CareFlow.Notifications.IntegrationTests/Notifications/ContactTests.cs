using CareFlow.Notifications.Api.Contacts;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>Contato lido do cofre na hora do envio: ausente, vazio ou cofre fora do ar.</summary>
public sealed class ContactTests(NotificationsApiFixture api) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await api.ResetChannelsAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task PatientUnknownToVault_IsNotSentNoContact()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        api.Contacts.Set(scenario.Tenant, scenario.Patient, ContactLookup.NotFound);
        var booked = await scenario.BookAsync();

        // Act
        await api.DispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");
        Assert.Equal(("NotSent", "NoContact"), (confirmation.Status, confirmation.NotSentReason));
        Assert.Contains("notification.confirmation.not-sent.no-contact", await scenario.AuditActionsAsync());
    }

    [Fact]
    public async Task PatientWithoutPhoneOrEmail_IsNotSentNoContact()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api, new PatientContact(null, " "));
        var booked = await scenario.BookAsync();

        // Act
        await api.DispatchAsync();

        // Assert
        Assert.Equal("NoContact", (await scenario.NotificationAsync(booked.AppointmentId, "Confirmation")).NotSentReason);
    }

    [Fact]
    public async Task OnlyEmail_SkipsPhoneChannelsWithoutCountingFailure()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api, new PatientContact(null, CareFlow.Testing.SyntheticPatients.Email()));
        var booked = await scenario.BookAsync();

        // Act
        await api.DispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");
        Assert.Equal("Email", confirmation.SentVia);
        Assert.Equal(
            [("WhatsApp", "NoContact"), ("Sms", "NoContact"), ("Email", "Delivered")],
            confirmation.Attempts.Select(a => (a.Channel, a.Outcome)));
    }

    [Fact]
    public async Task VaultUnavailable_RetriesInLaterRound()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var contact = scenario.Contact;
        api.Contacts.Set(scenario.Tenant, scenario.Patient, ContactLookup.Unavailable);
        var booked = await scenario.BookAsync();

        // Act 1: um ciclo só; repetir o ciclo pegaria as rodadas seguintes (o atraso entre rodadas é curto no teste).
        await api.DispatchOnceAsync();
        var afterOutage = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");

        // Act 2: o cofre volta.
        await scenario.WithContactAsync(contact);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        await api.DispatchAsync();

        // Assert
        Assert.Equal(("Pending", 1), (afterOutage.Status, afterOutage.Rounds));
        var confirmation = await scenario.NotificationAsync(booked.AppointmentId, "Confirmation");
        Assert.Equal(("Sent", 1), (confirmation.Status, confirmation.Rounds));
    }
}
