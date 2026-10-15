using System.Net;
using System.Net.Http.Json;

using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Api.Features.FaultInjection;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>
/// Falha injetada por clínica (ADR-0010): uma clínica isolada do demo derruba o WhatsApp só para si. O circuito
/// dela abre e fecha sem tocar no compartilhado nem no de outra clínica isolada; clínicas comuns não podem injetar.
/// </summary>
public sealed class ClinicFaultIsolationTests(NotificationsApiFixture api) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid VisitorA => NotificationsApiFixture.IsolatedClinics[0];

    private static Guid VisitorB => NotificationsApiFixture.IsolatedClinics[1];

    public async ValueTask InitializeAsync() => await api.ResetChannelsAsync();

    public async ValueTask DisposeAsync() => await api.ResetChannelsAsync();

    private string PartitionOf(Guid clinic) => api.Services.GetRequiredService<ClinicIsolation>().PartitionFor(clinic);

    private async Task<(Scenario Scenario, Guid Appointment)> BookAndDispatchAsync(Guid? tenant = null)
    {
        var scenario = await Scenario.ReadyAsync(api, tenant: tenant);
        var booked = await scenario.BookAsync();
        await api.DispatchAsync();
        return (scenario, booked.AppointmentId);
    }

    private Task<HttpResponseMessage> PutFaultAsync(Guid clinic, string channel, string? mode, int seconds = 60) =>
        api.CreateClient(clinic, Admin).PutAsJsonAsync($"/v1/fault-injection/{channel}", new ChannelFaultRequest(mode, seconds), Ct);

    [Fact]
    public async Task VisitorDropsWhatsApp_OnlyTheirNoticesFallBack_AndOnlyTheirCircuitOpens()
    {
        // Arrange
        Assert.Equal(HttpStatusCode.NoContent, (await PutFaultAsync(VisitorA, "whatsapp", "Unavailable")).StatusCode);

        // Act: duas reservas do visitante A abrem o circuito dele; B e uma clínica comum reservam também.
        await BookAndDispatchAsync(VisitorA);
        var (a, appointmentA) = await BookAndDispatchAsync(VisitorA);
        var (b, appointmentB) = await BookAndDispatchAsync(VisitorB);
        var (shared, appointmentShared) = await BookAndDispatchAsync();

        // Assert
        Assert.Equal("Sms", (await a.NotificationAsync(appointmentA, "Confirmation")).SentVia);
        Assert.Equal("WhatsApp", (await b.NotificationAsync(appointmentB, "Confirmation")).SentVia);
        Assert.Equal("WhatsApp", (await shared.NotificationAsync(appointmentShared, "Confirmation")).SentVia);

        Assert.Equal(ChannelCircuitState.Open, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(VisitorA)));
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(VisitorB)));
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp));

        var view = await api.CreateClient(VisitorA, Admin).GetFromJsonAsync<ClinicFaultsResponse>("/v1/fault-injection", Ct);
        var whatsapp = Assert.Single(view!.Channels, c => c.Channel == "whatsapp");
        Assert.Equal(("Unavailable", "Open"), (whatsapp.Fault, whatsapp.Circuit));
        Assert.StartsWith("isolated-", view.Partition, StringComparison.Ordinal);
        Assert.DoesNotContain(VisitorA.ToString(), view.Partition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClearingTheFault_CircuitClosesAfterBreak_AndWhatsAppDeliversAgain()
    {
        // Arrange
        await PutFaultAsync(VisitorA, "whatsapp", "Error");
        await BookAndDispatchAsync(VisitorA);
        await BookAndDispatchAsync(VisitorA);
        Assert.Equal(ChannelCircuitState.Open, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(VisitorA)));

        // Act
        var cleared = await api.CreateClient(VisitorA, Admin).DeleteAsync("/v1/fault-injection/whatsapp", Ct);
        await Task.Delay(NotificationsApiFixture.BreakDuration + TimeSpan.FromMilliseconds(200), Ct);
        var (scenario, appointment) = await BookAndDispatchAsync(VisitorA);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal("WhatsApp", (await scenario.NotificationAsync(appointment, "Confirmation")).SentVia);
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(VisitorA)));
    }

    [Fact]
    public async Task ExpiredFault_NoLongerApplies()
    {
        // Arrange: a falha venceu (o visitante saiu sem desligar).
        await PutFaultAsync(VisitorA, "whatsapp", "Unavailable");
        await api.ExecuteAsAdminAsync("UPDATE clinic_channel_faults SET expires_at = now() - interval '1 second'");

        // Act
        var (scenario, appointment) = await BookAndDispatchAsync(VisitorA);

        // Assert
        Assert.Equal("WhatsApp", (await scenario.NotificationAsync(appointment, "Confirmation")).SentVia);
        var view = await api.CreateClient(VisitorA, Admin).GetFromJsonAsync<ClinicFaultsResponse>("/v1/fault-injection", Ct);
        Assert.All(view!.Channels, c => Assert.Null(c.Fault));
    }

    [Fact]
    public async Task OrdinaryClinic_CannotInjectFaults()
    {
        // Act
        var ordinary = Guid.NewGuid();
        var put = await PutFaultAsync(ordinary, "whatsapp", "Unavailable");
        var get = await api.CreateClient(ordinary, Admin).GetAsync("/v1/fault-injection", Ct);
        var delete = await api.CreateClient(ordinary, Admin).DeleteAsync("/v1/fault-injection/whatsapp", Ct);

        // Assert
        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden], [put.StatusCode, get.StatusCode, delete.StatusCode]);
        Assert.Equal(0, await api.ExecuteAsAdminAsync("UPDATE clinic_channel_faults SET mode = mode"));
    }

    [Fact]
    public async Task FaultInjection_RequiresAdminScope()
    {
        // Act
        var reader = await api.CreateClient(VisitorA, Read).PutAsJsonAsync("/v1/fault-injection/whatsapp", new ChannelFaultRequest("Unavailable", 60), Ct);
        var anonymous = await api.CreateClient(null).GetAsync("/v1/fault-injection", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData("whatsapp", null, 60)]
    [InlineData("whatsapp", "None", 60)]
    [InlineData("whatsapp", "unavailable", 60)]
    [InlineData("whatsapp", "7", 60)]
    [InlineData("whatsapp", "Unavailable", 9)]
    [InlineData("whatsapp", "Unavailable", 901)]
    [InlineData("telegram", "Unavailable", 60)]
    [InlineData("WhatsApp", "Unavailable", 60)]
    public async Task InvalidRequests_AreRejected(string channel, string? mode, int seconds)
    {
        // Act
        var response = await PutFaultAsync(VisitorA, channel, mode, seconds);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUnknownChannel_IsRejected()
    {
        var response = await api.CreateClient(VisitorA, Admin).DeleteAsync("/v1/fault-injection/pombo", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SettingTwice_KeepsOneRow_WithTheLatestMode()
    {
        // Act
        await PutFaultAsync(VisitorA, "sms", "Unavailable");
        await PutFaultAsync(VisitorA, "sms", "Latency", 30);

        // Assert
        var view = await api.CreateClient(VisitorA, Admin).GetFromJsonAsync<ClinicFaultsResponse>("/v1/fault-injection", Ct);
        Assert.Equal("Latency", Assert.Single(view!.Channels, c => c.Channel == "sms").Fault);
    }
}
