using CareFlow.Contracts.Appointments;
using CareFlow.Notifications.Api.Contacts;
using CareFlow.Notifications.Api.Features.Notifications;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>Uma clínica e um paciente sintético com contato e consentimento para notificações.</summary>
internal sealed class Scenario(NotificationsApiFixture api)
{
    public Guid Tenant { get; } = Guid.NewGuid();

    public Guid Patient { get; } = Guid.NewGuid();

    public PatientContact Contact { get; private set; } = SyntheticContact();

    public HttpClient Reader => api.CreateClient(Tenant, Read);

    public static async Task<Scenario> ReadyAsync(NotificationsApiFixture api, PatientContact? contact = null)
    {
        var scenario = new Scenario(api);
        await scenario.WithContactAsync(contact ?? scenario.Contact);
        await api.GrantNotificationsAsync(scenario.Tenant, scenario.Patient);
        return scenario;
    }

    public Task WithContactAsync(PatientContact contact)
    {
        Contact = contact;
        api.Contacts.Set(Tenant, Patient, ContactLookup.Found(contact));
        return Task.CompletedTask;
    }

    public async Task<AppointmentBooked> BookAsync(DateTimeOffset? startsAt = null)
    {
        var booked = Booked(Patient, startsAt);
        await api.DeliverAsync(Tenant, booked);
        return booked;
    }

    public Task<IReadOnlyList<NotificationResponse>> NotificationsAsync(Guid appointmentId) => Reader.NotificationsAsync(appointmentId);

    public async Task<NotificationResponse> NotificationAsync(Guid appointmentId, string kind) =>
        Assert.Single(await NotificationsAsync(appointmentId), n => n.Kind == kind);

    public async Task<string?[]> AuditActionsAsync() =>
        [.. (await api.OutboxAsync(Tenant)).Where(row => row.AuditAction is not null).Select(row => row.AuditAction)];
}
