using System.Net;
using System.Net.Http.Json;

using CareFlow.Contracts.Consents;
using CareFlow.Notifications.Api.Features.Settings;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

using Npgsql;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>Cada endpoint e cada evento só enxergam a clínica do token ou da mensagem; o banco repete a regra (RLS).</summary>
public sealed class ClinicIsolationTests(NotificationsApiFixture api) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await api.ResetChannelsAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ListNotifications_OfAnotherClinic_IsNotFound()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();

        // Act
        var own = await api.CreateClient(scenario.Tenant, Read).GetAsync($"/v1/appointments/{booked.AppointmentId}/notifications", Ct);
        var foreign = await api.CreateClient(Guid.NewGuid(), Read, Admin).GetAsync($"/v1/appointments/{booked.AppointmentId}/notifications", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("Notificacoes.ConsultaSemAvisos", await foreign.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Settings_AreIsolatedPerClinic()
    {
        // Arrange
        var clinicA = Guid.NewGuid();
        var clinicB = Guid.NewGuid();
        var adminA = api.CreateClient(clinicA, Admin);

        // Act
        var put = await adminA.PutAsJsonAsync("/v1/settings", new { displayName = "Clinica A", timeZone = "America/Sao_Paulo", channelOrder = Channels("Sms"), reminderLeadMinutes = 60 }, Ct);
        var seenByA = await adminA.GetFromJsonAsync<ClinicSettingsResponse>("/v1/settings", Ct);
        var seenByB = await api.CreateClient(clinicB, Admin).GetFromJsonAsync<ClinicSettingsResponse>("/v1/settings", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(("Clinica A", false), (seenByA!.DisplayName, seenByA.IsDefault));
        Assert.Equal(["Sms"], seenByA.ChannelOrder);
        Assert.Equal(("sua clínica", true), (seenByB!.DisplayName, seenByB.IsDefault));
        Assert.Equal(["WhatsApp", "Sms", "Email"], seenByB.ChannelOrder);
    }

    [Fact]
    public async Task EventsOfAnotherClinic_DoNotTouchThisClinic()
    {
        // Arrange: o mesmo identificador de consulta e de paciente em duas clínicas (o isolamento não pode depender de unicidade).
        var scenario = await Scenario.ReadyAsync(api);
        var otherClinic = Guid.NewGuid();
        var booked = await scenario.BookAsync();

        // Act
        await api.DeliverAsync(otherClinic, Cancelled(booked));
        await api.DeliverAsync(otherClinic, new ConsentRevoked(scenario.Patient, ConsentPurposes.Notifications, 9, DateTimeOffset.UtcNow));
        await api.DispatchAsync();

        // Assert
        var notifications = await scenario.NotificationsAsync(booked.AppointmentId);
        Assert.Equal("Sent", notifications.Single(n => n.Kind == "Confirmation").Status);
        Assert.Equal("Pending", notifications.Single(n => n.Kind == "Reminder").Status);
        Assert.DoesNotContain(notifications, n => n.Kind == "Cancellation");
        var foreign = await api.CreateClient(otherClinic, Read).NotificationsAsync(booked.AppointmentId);
        Assert.Equal(("Cancellation", "NotSent"), (Assert.Single(foreign).Kind, foreign[0].Status));
    }

    [Fact]
    public async Task Database_ApplicationLoginWithoutClinic_SeesNothing()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        await scenario.BookAsync();

        // Act: conexão direta com o login da API, sem a clínica na sessão.
        await using var connection = new NpgsqlConnection(api.ApplicationConnectionString);
        await connection.OpenAsync(Ct);
        long Count(string table)
        {
            using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            return (long)command.ExecuteScalar()!;
        }

        // Assert
        foreach (var table in Channels("notifications", "notification_jobs", "appointment_tracks", "notification_consents", "clinic_settings", "delivery_attempts", "inbox_messages"))
        {
            Assert.Equal(0, Count(table));
        }

        // A API só escreve no Outbox; quem lê é o relay.
        var outbox = Assert.Throws<PostgresException>(() => Count("outbox_messages"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, outbox.SqlState);
    }

    [Fact]
    public async Task Database_RelayLogin_SeesQueueButNotNotifications()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);
        await scenario.BookAsync();
        await using var connection = new NpgsqlConnection(api.RelayConnectionString);
        await connection.OpenAsync(Ct);

        // Act
        await using var queue = new NpgsqlCommand("SELECT count(*) FROM notification_jobs WHERE tenant_id = $1", connection) { Parameters = { new() { Value = scenario.Tenant } } };
        var queued = (long)(await queue.ExecuteScalarAsync(Ct))!;
        await using var business = new NpgsqlCommand("SELECT count(*) FROM notifications", connection);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => business.ExecuteScalarAsync(Ct));

        // Assert
        Assert.Equal(2, queued);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }
}
