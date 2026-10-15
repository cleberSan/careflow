using CareFlow.Contracts.Appointments;
using CareFlow.Contracts.Consents;
using CareFlow.Contracts.Patients;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.Scheduling.IntegrationTests.Infrastructure.SchedulingClient;

namespace CareFlow.Scheduling.IntegrationTests.Appointments;

/// <summary>
/// Reserva e cancelamento publicam eventos de consulta pelo Outbox, na mesma transação, para o Notifications
/// avisar o paciente. Os eventos levam só pseudônimo, horário e motivo.
/// </summary>
public sealed class AppointmentEventsTests(SchedulingApiFixture api)
{
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<OutboxRow[]> EventsAsync(string type) =>
        [.. (await api.OutboxAsync(_tenant)).Where(row => row.Type == type)];

    [Fact]
    public async Task Book_PublishesAppointmentBookedWithSlotAndPseudonymOnly()
    {
        // Arrange
        var request = Request(Guid.NewGuid(), FutureSlot());

        // Act
        var booked = await (await api.CreateClient(_tenant, Write).BookAsync(request, NewKey(), Ct)).ReadAppointmentAsync();

        // Assert
        var payload = Assert.Single(await EventsAsync(AppointmentBooked.EventType)).Payload;
        Assert.Equal(booked.Id, payload.GetProperty("appointmentId").GetGuid());
        Assert.Equal(request.PatientRef, payload.GetProperty("patientRef").GetGuid());
        Assert.Equal(request.StartsAt, payload.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(request.EndsAt, payload.GetProperty("endsAt").GetDateTimeOffset());
        Assert.False(payload.TryGetProperty("professionalId", out _));
    }

    [Fact]
    public async Task Book_Replayed_DoesNotPublishTwice()
    {
        // Arrange
        var client = api.CreateClient(_tenant, Write);
        var request = Request(Guid.NewGuid(), FutureSlot());
        var key = NewKey();

        // Act
        await client.BookAsync(request, key, Ct);
        await client.BookAsync(request, key, Ct);

        // Assert
        Assert.Single(await EventsAsync(AppointmentBooked.EventType));
    }

    [Fact]
    public async Task Cancel_PublishesAppointmentCancelledWithRequestedReason()
    {
        // Arrange
        var client = api.CreateClient(_tenant, Write);
        var booked = await (await client.BookAsync(Request(Guid.NewGuid(), FutureSlot()), NewKey(), Ct)).ReadAppointmentAsync();

        // Act
        await client.PostAsync($"/v1/appointments/{booked.Id}/cancel", null, Ct);
        await client.PostAsync($"/v1/appointments/{booked.Id}/cancel", null, Ct);

        // Assert: o segundo cancelamento é recusado e não publica nada.
        var payload = Assert.Single(await EventsAsync(AppointmentCancelled.EventType)).Payload;
        Assert.Equal(booked.Id, payload.GetProperty("appointmentId").GetGuid());
        Assert.Equal(booked.StartsAt, payload.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(AppointmentCancellationReasons.Requested, payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ConsentRevocation_PublishesCancellationWithConsentRevokedReason()
    {
        // Arrange
        var patient = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await api.DeliverAsync(_tenant, new PatientRegistered(patient, now));
        await api.DeliverAsync(_tenant, new ConsentGranted(patient, ConsentPurposes.Scheduling, "consent", "v1", 1, now));
        var booked = await (await api.CreateClientWithoutSeeding(_tenant, Write)
            .BookAsync(Request(Guid.NewGuid(), FutureSlot(), patientRef: patient), NewKey(), Ct)).ReadAppointmentAsync();

        // Act
        await api.DeliverAsync(_tenant, new ConsentRevoked(patient, ConsentPurposes.Scheduling, 2, now));

        // Assert
        var payload = Assert.Single(await EventsAsync(AppointmentCancelled.EventType)).Payload;
        Assert.Equal(booked.Id, payload.GetProperty("appointmentId").GetGuid());
        Assert.Equal(patient, payload.GetProperty("patientRef").GetGuid());
        Assert.Equal(AppointmentCancellationReasons.ConsentRevoked, payload.GetProperty("reason").GetString());
    }
}
