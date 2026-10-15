extern alias notifications;

using CareFlow.EndToEndTests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

using SimulatedProviderLedger = notifications::CareFlow.Notifications.Api.Channels.Simulated.SimulatedProviderLedger;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Do agendamento ao aviso, com RabbitMQ real e o WhatsApp derrubado por falha injetada: a agenda publica pelo
/// Outbox, o Notifications consome pelo Inbox, lê o contato no cofre com token de serviço e cai para o SMS.
/// Cada leitura de contato e cada envio chegam à trilha de auditoria.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class NotificationFlowTests(CareFlowSystem system)
{
    private static DateTimeOffset Slot(int day, int hour) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(day).AddHours(hour);

    [Fact]
    public async Task Booking_WithWhatsAppDown_NotifiesBySms_AndCancellationToo()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        var phone = api.LastPatient.Phone;
        await api.GrantAsync(patient);
        await api.GrantAsync(patient, "notifications");

        // Act 1: agendamento.
        var appointment = await api.BookWhenEligibleAsync(patient, Slot(15, 13));

        // Assert 1: confirmação pelo SMS depois de o WhatsApp falhar; lembrete agendado para depois.
        var notices = await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada");
        var confirmation = notices.Single(n => n.Kind == "Confirmation");
        Assert.Equal("Sms", confirmation.SentVia);
        Assert.Equal("WhatsApp", confirmation.Attempts[0].Channel);
        Assert.NotEqual("Delivered", confirmation.Attempts[0].Outcome);
        Assert.Equal(("Sms", "Delivered"), (confirmation.Attempts[^1].Channel, confirmation.Attempts[^1].Outcome));
        Assert.Equal("Pending", notices.Single(n => n.Kind == "Reminder").Status);

        // Act 2: cancelamento.
        await api.CancelAsync(appointment);

        // Assert 2: aviso de cancelamento também pelo SMS e o lembrete descartado.
        notices = await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Cancellation" && n.Status == "Sent"),
            description: "aviso de cancelamento enviado");
        Assert.Equal("Superseded", notices.Single(n => n.Kind == "Reminder").Status);

        // O paciente recebeu exatamente duas mensagens, sem nome nem dado de saúde.
        var ledger = system.Notifications.Services.GetRequiredService<SimulatedProviderLedger>();
        var received = ledger.Messages.Where(m => m.RecipientFingerprint == ledger.Fingerprint(phone)).ToArray();
        Assert.Equal(2, received.Length);
        Assert.All(received, m => Assert.Equal("sms", m.Channel.ToString().ToLowerInvariant()));
        Assert.All(received, m => Assert.DoesNotContain(api.LastPatient.FullName.Split(' ')[0], m.Text, StringComparison.Ordinal));

        // Trilha: o cofre registra cada leitura de contato com o Notifications como ator, e o Notifications cada envio.
        var trail = await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => entries.Count(e => e.Action == "patient.contact.read") >= 2
                       && entries.Any(e => e.Action == "notification.confirmation.sent.sms")
                       && entries.Any(e => e.Action == "notification.cancellation.sent.sms"),
            description: "leituras de contato e envios auditados");
        Assert.All(trail.Where(e => e.Action == "patient.contact.read"), e => Assert.Equal(("patient-vault", "careflow-notifications"), (e.SourceService, e.Actor)));
        Assert.All(trail.Where(e => e.SourceService == "notifications"), e => Assert.Equal("careflow-notifications", e.Actor));

        var verification = await api.VerifyChainAsync();
        Assert.True(verification.Intact, verification.Failure);
    }

    [Fact]
    public async Task WithoutNotificationConsent_NothingIsSent_AndTheReasonIsAudited()
    {
        // Arrange: só o consentimento de agendamento; o de avisos é concedido e revogado antes da reserva.
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        await api.GrantAsync(patient);
        await api.GrantAsync(patient, "notifications");
        await api.RevokeAsync(patient, "notifications");

        // A revogação precisa chegar ao Notifications antes da reserva (vêm por filas diferentes).
        var revoked = await LastConsentEventAsync(api.Clinic);
        await Eventually.SucceedsAsync(async () => await CareFlowApi.InboxCountAsync(system.Notifications, revoked) == 1);

        // Act
        var appointment = await api.BookWhenEligibleAsync(patient, Slot(16, 13));

        // Assert
        var notices = await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Confirmation" && n.Status != "Pending"),
            description: "confirmação encerrada");
        var confirmation = notices.Single(n => n.Kind == "Confirmation");
        Assert.Equal(("NotSent", "NoConsent"), (confirmation.Status, confirmation.NotSentReason));
        Assert.Empty(confirmation.Attempts);

        var trail = await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => entries.Any(e => e.Action == "notification.confirmation.not-sent.no-consent"),
            description: "motivo do não envio auditado");
        Assert.DoesNotContain(trail, e => e.Action == "patient.contact.read");
    }

    private async Task<Guid> LastConsentEventAsync(Guid clinic)
    {
        await using var connection = new Npgsql.NpgsqlConnection(system.Consent.AdminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT id FROM outbox_messages WHERE tenant_id = $1 AND type = 'consent.revoked' ORDER BY id DESC LIMIT 1", connection)
        {
            Parameters = { new() { Value = clinic } },
        };
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
