using System.Net;
using System.Net.Http.Json;

using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Api.Features.FaultInjection;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>
/// Falha injetada por clínica (ADR-0010): a clínica sintética de uma sessão do Painel se registra numa partição
/// isolada e derruba o WhatsApp só para si. O circuito dela abre e fecha sem tocar no compartilhado nem no de outra
/// sessão; clínicas sem partição não podem injetar, e uma partição só muda de dono depois de liberada.
/// </summary>
public sealed class ClinicFaultIsolationTests(NotificationsApiFixture api) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Clínicas novas a cada teste, como cada sessão do Painel ganha a sua.
    private readonly Guid _visitorA = Guid.NewGuid();
    private readonly Guid _visitorB = Guid.NewGuid();

    private Guid VisitorA => _visitorA;

    private Guid VisitorB => _visitorB;

    public async ValueTask InitializeAsync()
    {
        await api.ResetChannelsAsync();
        Assert.Equal(HttpStatusCode.OK, (await LeaseAsync(VisitorA, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LeaseAsync(VisitorB, 2)).StatusCode);
    }

    public async ValueTask DisposeAsync() => await api.ResetChannelsAsync();

    private static string PartitionOf(int slot) => ClinicIsolation.Label(slot);

    private Task<HttpResponseMessage> LeaseAsync(Guid clinic, int slot, int seconds = 300) =>
        api.CreateClient(clinic, Admin).PutAsJsonAsync("/v1/fault-injection/partition", new PartitionRequest(slot, seconds), Ct);

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

        Assert.Equal(ChannelCircuitState.Open, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(1)));
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(2)));
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp));

        var view = await api.CreateClient(VisitorA, Admin).GetFromJsonAsync<ClinicFaultsResponse>("/v1/fault-injection", Ct);
        var whatsapp = Assert.Single(view!.Channels, c => c.Channel == "whatsapp");
        Assert.Equal(("Unavailable", "Open"), (whatsapp.Fault, whatsapp.Circuit));
        Assert.Equal("isolated-01", view.Partition);
        Assert.DoesNotContain(VisitorA.ToString(), view.Partition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClearingTheFault_CircuitClosesAfterBreak_AndWhatsAppDeliversAgain()
    {
        // Arrange
        await PutFaultAsync(VisitorA, "whatsapp", "Error");
        await BookAndDispatchAsync(VisitorA);
        await BookAndDispatchAsync(VisitorA);
        Assert.Equal(ChannelCircuitState.Open, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(1)));

        // Act
        var cleared = await api.CreateClient(VisitorA, Admin).DeleteAsync("/v1/fault-injection/whatsapp", Ct);
        await Task.Delay(NotificationsApiFixture.BreakDuration + TimeSpan.FromMilliseconds(200), Ct);
        var (scenario, appointment) = await BookAndDispatchAsync(VisitorA);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal("WhatsApp", (await scenario.NotificationAsync(appointment, "Confirmation")).SentVia);
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(1)));
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
    public async Task ExpiredPartition_SendsThroughTheSharedCircuit_AndCannotInjectAnymore()
    {
        // Arrange: falha ligada, mas a sessão venceu sem liberar a partição.
        await PutFaultAsync(VisitorA, "whatsapp", "Unavailable");
        await api.ExecuteAsAdminAsync("UPDATE clinic_partitions SET expires_at = now() - interval '1 second'");

        // Act
        var (scenario, appointment) = await BookAndDispatchAsync(VisitorA);
        var put = await PutFaultAsync(VisitorA, "whatsapp", "Unavailable");

        // Assert: a falha da clínica não vale mais fora da partição, e o circuito isolado não foi tocado.
        Assert.Equal("WhatsApp", (await scenario.NotificationAsync(appointment, "Confirmation")).SentVia);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(1)));
    }

    [Fact]
    public async Task TakenPartition_IsRefusedToAnotherClinic()
    {
        // Act: um visitante novo tenta a partição que está com A.
        var intruder = await LeaseAsync(Guid.NewGuid(), 1);
        var renewal = await LeaseAsync(VisitorA, 1, 600);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, intruder.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
        var view = await renewal.Content.ReadFromJsonAsync<PartitionView>(Ct);
        Assert.Equal("isolated-01", view!.Partition);
        Assert.True(view.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(500));
    }

    [Fact]
    public async Task ReleasedPartition_GoesToTheNextSession_WithClosedCircuitAndNoFaults()
    {
        // Arrange: A abre o circuito da partição 1 e sai.
        await PutFaultAsync(VisitorA, "whatsapp", "Unavailable");
        await BookAndDispatchAsync(VisitorA);
        await BookAndDispatchAsync(VisitorA);
        Assert.Equal(ChannelCircuitState.Open, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(1)));
        var released = await api.CreateClient(VisitorA, Admin).DeleteAsync("/v1/fault-injection/partition", Ct);

        // Act
        var next = Guid.NewGuid();
        var leased = await LeaseAsync(next, 1);
        var (scenario, appointment) = await BookAndDispatchAsync(next);

        // Assert: a sessão nova não herda falha nem circuito; A não injeta mais nada.
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.OK), (released.StatusCode, leased.StatusCode));
        Assert.Equal("WhatsApp", (await scenario.NotificationAsync(appointment, "Confirmation")).SentVia);
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp, PartitionOf(1)));
        Assert.Equal(HttpStatusCode.Forbidden, (await PutFaultAsync(VisitorA, "whatsapp", "Unavailable")).StatusCode);
        Assert.Equal(0, await api.ExecuteAsAdminAsync($"UPDATE clinic_channel_faults SET mode = mode WHERE tenant_id = '{VisitorA}'"));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(NotificationsApiFixture.IsolatedSlots + 1, 300)]
    [InlineData(-1, 300)]
    [InlineData(3, ClinicIsolation.MinLeaseSeconds - 1)]
    [InlineData(3, ClinicIsolation.MaxLeaseSeconds + 1)]
    public async Task InvalidPartitionRequests_AreRejected(int slot, int seconds)
    {
        // Act
        var response = await LeaseAsync(Guid.NewGuid(), slot, seconds);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        var readerLease = await api.CreateClient(Guid.NewGuid(), Read).PutAsJsonAsync("/v1/fault-injection/partition", new PartitionRequest(3, 300), Ct);
        var anonymous = await api.CreateClient(null).GetAsync("/v1/fault-injection", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readerLease.StatusCode);
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
