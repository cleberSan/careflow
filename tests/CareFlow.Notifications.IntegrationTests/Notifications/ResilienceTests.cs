using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

using CareFlow.Notifications.Api.Channels;
using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>
/// Fallback entre canais, circuit breaker (aberto e meio-aberto), retentativa sem duplicar mensagem e falhas
/// injetadas, com o pipeline real do Microsoft.Extensions.Http.Resilience sobre os provedores simulados.
/// </summary>
public sealed class ResilienceTests(NotificationsApiFixture api) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await api.ResetChannelsAsync();

    public async ValueTask DisposeAsync() => await api.ResetChannelsAsync();

    private int MessagesTo(Scenario scenario) =>
        api.Ledger.Messages.Count(m => m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Phone!)
            || m.RecipientFingerprint == api.Ledger.Fingerprint(scenario.Contact.Email!));

    private async Task<(Scenario Scenario, Guid AppointmentId)> BookAndDispatchAsync()
    {
        var scenario = await Scenario.ReadyAsync(api);
        var booked = await scenario.BookAsync();
        await api.DispatchAsync();
        return (scenario, booked.AppointmentId);
    }

    [Fact]
    public async Task WhatsAppDown_FallsBackToSms()
    {
        // Arrange
        api.InjectFaults((Channel.WhatsApp, FaultMode.Unavailable));

        // Act
        var (scenario, appointment) = await BookAndDispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(appointment, "Confirmation");
        Assert.Equal(("Sent", "Sms"), (confirmation.Status, confirmation.SentVia));
        Assert.Equal([("WhatsApp", "Failed"), ("Sms", "Delivered")], confirmation.Attempts.Select(a => (a.Channel, a.Outcome)));
        Assert.Equal("HttpRequestException", confirmation.Attempts[0].Error);
        Assert.Equal(1, MessagesTo(scenario));
        Assert.Contains("notification.confirmation.sent.sms", await scenario.AuditActionsAsync());
    }

    [Fact]
    public async Task LatencyBeyondAttemptTimeout_FallsBack()
    {
        // Arrange: 1 s de atraso contra 300 ms de timeout por tentativa.
        api.InjectFaults((Channel.WhatsApp, FaultMode.Latency));

        // Act
        var (scenario, appointment) = await BookAndDispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(appointment, "Confirmation");
        Assert.Equal("Sms", confirmation.SentVia);
        Assert.Equal("TimeoutRejectedException", confirmation.Attempts[0].Error);
    }

    [Fact]
    public async Task ProviderErrors_FallBack()
    {
        // Arrange
        api.InjectFaults((Channel.WhatsApp, FaultMode.Error), (Channel.Sms, FaultMode.Error));

        // Act
        var (scenario, appointment) = await BookAndDispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(appointment, "Confirmation");
        Assert.Equal("Email", confirmation.SentVia);
        Assert.Equal(["http-503", "http-503", null], confirmation.Attempts.Select(a => a.Error));
    }

    [Fact]
    public async Task LostResponse_RetryUsesSameKey_AndPatientGetsOneMessage()
    {
        // Arrange: o provedor aceita e a resposta se perde; a retentativa recebe o replay.
        api.InjectFaults((Channel.WhatsApp, FaultMode.LostResponse));

        // Act
        var (scenario, appointment) = await BookAndDispatchAsync();

        // Assert
        var confirmation = await scenario.NotificationAsync(appointment, "Confirmation");
        Assert.Equal("WhatsApp", confirmation.SentVia);
        Assert.Single(confirmation.Attempts);
        Assert.Equal(1, MessagesTo(scenario));
    }

    [Fact]
    public async Task ReprocessingAfterLostCommit_DoesNotSendAgain()
    {
        // Arrange: enviado, mas o processo "caiu" antes de gravar (o aviso volta a pendente com a fila).
        var (scenario, appointment) = await BookAndDispatchAsync();
        var first = await scenario.NotificationAsync(appointment, "Confirmation");
        await api.ExecuteAsAdminAsync(
            "UPDATE notifications SET status = 'Pending', sent_via = NULL, provider_message_id = NULL, completed_at = NULL WHERE id = $1", first.Id);
        await api.ExecuteAsAdminAsync(
            "INSERT INTO notification_jobs (notification_id, tenant_id, due_at) VALUES ($1, $2, now())", first.Id, scenario.Tenant);

        // Act
        await api.DispatchAsync();

        // Assert: mesma chave, mesmo id de mensagem do provedor, nenhuma segunda entrega.
        var again = await scenario.NotificationAsync(appointment, "Confirmation");
        Assert.Equal("Sent", again.Status);
        Assert.Equal(1, MessagesTo(scenario));
        Assert.Equal(2, again.Attempts.Count);
    }

    [Fact]
    public async Task Gateway_SameKeyTwice_ReturnsSameProviderMessage()
    {
        // Arrange
        var gateway = api.Services.GetRequiredService<ChannelGateway>();
        var key = Guid.NewGuid().ToString("N");

        // Act
        var first = await gateway.SendAsync(Channel.Sms, key, CareFlow.Testing.SyntheticPatients.Phone(), "CareFlow: teste", Ct);
        var second = await gateway.SendAsync(Channel.Sms, key, CareFlow.Testing.SyntheticPatients.Phone(), "CareFlow: teste", Ct);

        // Assert
        Assert.Equal(ChannelResultKind.Delivered, first.Kind);
        Assert.Equal(first.ProviderMessageId, second.ProviderMessageId);
        Assert.Single(api.Ledger.Messages, m => m.IdempotencyKey == key);
    }

    [Fact]
    public async Task CircuitOpens_ThenChannelIsSkippedWithoutCallingIt_AndHealthShowsDegraded()
    {
        // Arrange
        api.InjectFaults((Channel.WhatsApp, FaultMode.Unavailable));
        await BookAndDispatchAsync();

        // Act
        var (scenario, appointment) = await BookAndDispatchAsync();

        // Assert
        Assert.Equal(ChannelCircuitState.Open, api.Circuits.StateOf(Channel.WhatsApp));
        var confirmation = await scenario.NotificationAsync(appointment, "Confirmation");
        Assert.Equal([("WhatsApp", "CircuitOpen"), ("Sms", "Delivered")], confirmation.Attempts.Select(a => (a.Channel, a.Outcome)));

        using var health = JsonDocument.Parse(await api.CreateClient(null).GetStringAsync("/health/channels", Ct));
        Assert.Equal("Degraded", health.RootElement.GetProperty("status").GetString());
        Assert.Equal("Open", health.RootElement.GetProperty("channels").GetProperty("whatsapp").GetString());
        Assert.Equal("Closed", health.RootElement.GetProperty("channels").GetProperty("sms").GetString());
    }

    [Fact]
    public async Task CircuitHalfOpen_FailedProbeReopens_SuccessfulProbeCloses()
    {
        // Arrange
        var transitions = new ConcurrentQueue<(Channel, ChannelCircuitState)>();
        api.Circuits.StateChanged += (channel, state) => transitions.Enqueue((channel, state));
        api.InjectFaults((Channel.WhatsApp, FaultMode.Unavailable));
        await BookAndDispatchAsync();
        Assert.Equal(ChannelCircuitState.Open, api.Circuits.StateOf(Channel.WhatsApp));

        // Act 1: passa o tempo de abertura, o WhatsApp continua fora; a tentativa de teste falha.
        await Task.Delay(NotificationsApiFixture.BreakDuration + TimeSpan.FromMilliseconds(200), Ct);
        var (failedProbe, failedAppointment) = await BookAndDispatchAsync();

        // Act 2: o WhatsApp volta; a próxima tentativa de teste passa e fecha o circuito.
        api.InjectFaults();
        await Task.Delay(NotificationsApiFixture.BreakDuration + TimeSpan.FromMilliseconds(200), Ct);
        var (recovered, recoveredAppointment) = await BookAndDispatchAsync();

        // Assert
        var whatsapp = transitions.Where(t => t.Item1 == Channel.WhatsApp).Select(t => t.Item2).ToArray();
        Assert.Equal(
            [ChannelCircuitState.Open, ChannelCircuitState.HalfOpen, ChannelCircuitState.Open, ChannelCircuitState.HalfOpen, ChannelCircuitState.Closed],
            whatsapp);
        Assert.Equal("Sms", (await failedProbe.NotificationAsync(failedAppointment, "Confirmation")).SentVia);
        Assert.Equal("WhatsApp", (await recovered.NotificationAsync(recoveredAppointment, "Confirmation")).SentVia);
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp));
    }

    [Fact]
    public async Task AllChannelsDown_RetriesRoundsThenGivesUp()
    {
        // Arrange
        api.InjectFaults((Channel.WhatsApp, FaultMode.Unavailable), (Channel.Sms, FaultMode.Unavailable), (Channel.Email, FaultMode.Unavailable));
        var (scenario, appointment) = await BookAndDispatchAsync();

        // Act: novas rodadas até o limite (3 nos testes).
        for (var i = 0; i < 20 && (await scenario.NotificationAsync(appointment, "Confirmation")).Status == "Pending"; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), Ct);
            await api.DispatchAsync();
        }

        // Assert
        var confirmation = await scenario.NotificationAsync(appointment, "Confirmation");
        Assert.Equal(("Failed", 3), (confirmation.Status, confirmation.Rounds));
        Assert.Equal(9, confirmation.Attempts.Count);
        Assert.Equal(0, MessagesTo(scenario));
        Assert.Contains("notification.confirmation.failed", await scenario.AuditActionsAsync());
    }

    [Fact]
    public async Task FaultModes_AreIgnoredWhileInjectionIsDisabled()
    {
        // Arrange: WhatsApp configurado como fora do ar, mas a chave geral está desligada.
        api.ConfigureFaults(enabled: false, (Channel.WhatsApp, FaultMode.Unavailable));

        // Act
        var (scenario, appointment) = await BookAndDispatchAsync();

        // Assert
        Assert.Equal("WhatsApp", (await scenario.NotificationAsync(appointment, "Confirmation")).SentVia);
        Assert.Equal(ChannelCircuitState.Closed, api.Circuits.StateOf(Channel.WhatsApp));
    }

    [Fact]
    public async Task ConcurrentDispatchers_SendEachNotificationOnce()
    {
        // Arrange
        var scenarios = new List<(Scenario Scenario, Guid Appointment)>();
        for (var i = 0; i < 8; i++)
        {
            var scenario = await Scenario.ReadyAsync(api);
            scenarios.Add((scenario, (await scenario.BookAsync()).AppointmentId));
        }

        // Act: duas "instâncias" disputando a mesma fila.
        await Task.WhenAll(api.DispatchAsync(), api.DispatchAsync());

        // Assert
        foreach (var (scenario, appointment) in scenarios)
        {
            var confirmation = await scenario.NotificationAsync(appointment, "Confirmation");
            Assert.Equal("Sent", confirmation.Status);
            Assert.Single(confirmation.Attempts);
            Assert.Equal(1, MessagesTo(scenario));
        }
    }

    [Fact]
    public async Task SimulatedProvider_RejectsMalformedRequests()
    {
        // Arrange
        var factory = api.Services.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(ChannelGateway.StatusClientName(Channel.Email));

        // Act
        var noKey = await client.PostAsJsonAsync("messages", new { to = "x", text = "y" }, Ct);
        using var badJson = new HttpRequestMessage(HttpMethod.Post, "messages") { Content = new StringContent("{", System.Text.Encoding.UTF8, "application/json") };
        badJson.Headers.Add("Idempotency-Key", "k1");
        var invalid = await client.SendAsync(badJson, Ct);
        using var tooLong = new HttpRequestMessage(HttpMethod.Post, "messages") { Content = JsonContent.Create(new { to = "x", text = new string('a', 2000) }) };
        tooLong.Headers.Add("Idempotency-Key", "k2");
        var longText = await client.SendAsync(tooLong, Ct);
        var unknownPath = await client.GetAsync("outra-coisa", Ct);
        var missing = await client.GetAsync("messages/nao-existe", Ct);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, longText.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknownPath.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }
}
