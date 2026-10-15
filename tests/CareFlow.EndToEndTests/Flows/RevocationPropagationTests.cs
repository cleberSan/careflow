using System.Net;

using CareFlow.EndToEndTests.Infrastructure;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// O caminho inteiro, com RabbitMQ real: cadastro no cofre e consentimento chegam à agenda por eventos, a
/// revogação cancela as consultas futuras e bloqueia novas, e cada acesso chega à trilha de auditoria.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class RevocationPropagationTests(CareFlowSystem system)
{
    private static DateTimeOffset Slot(int day, int hour) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(day).AddHours(hour);

    [Fact]
    public async Task Revocation_PropagatesToSchedulingAndAudit()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        await api.GrantAsync(patient);
        var first = await api.BookWhenEligibleAsync(patient, Slot(20, 9));
        var second = await api.BookWhenEligibleAsync(patient, Slot(21, 9));

        // Act
        await api.RevokeAsync(patient);

        // Assert: as duas consultas futuras são canceladas pela revogação...
        foreach (var id in new[] { first, second })
        {
            var appointment = await Eventually.GetAsync(
                () => api.AppointmentAsync(id),
                a => a.Status == "Cancelled",
                description: "consulta cancelada pela revogação");
            Assert.Equal("ConsentRevoked", appointment.CancellationReason);
        }

        // ...novas reservas são recusadas...
        var refused = await api.BookAsync(patient, Slot(22, 9));
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("Agenda.PacienteSemBaseLegal", refused.Code);

        // ...e a trilha da clínica tem cada passo, de cada serviço, com a cadeia íntegra.
        var expected = new[]
        {
            ("patient-vault", "patient.registered"),
            ("consent", "consent.granted"),
            ("scheduling", "appointment.booked"),
            ("consent", "consent.revoked"),
            ("scheduling", "appointment.cancelled.consent-revoked"),
        };
        var trail = await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => expected.All(e => entries.Any(x => x.SourceService == e.Item1 && x.Action == e.Item2)),
            description: "trilha de auditoria completa");
        Assert.Equal(2, trail.Count(e => e.Action == "appointment.cancelled.consent-revoked"));
        Assert.Contains(trail, e => e.Action == "appointment.cancelled.consent-revoked" && e.Actor == "careflow-scheduling");
        Assert.All(trail, e => Assert.Equal(patient, e.ResourceId));

        var verification = await api.VerifyChainAsync();
        Assert.True(verification.Intact, verification.Failure);
        Assert.Equal(verification.LastSequence, verification.EntriesVerified);
    }

    [Fact]
    public async Task Regrant_AfterRevocation_AllowsBookingAgain()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        await api.GrantAsync(patient);
        await api.BookWhenEligibleAsync(patient, Slot(30, 9));
        await api.RevokeAsync(patient);
        await Eventually.GetAsync(
            () => api.BookAsync(patient, Slot(31, 9)),
            attempt => attempt.Code == "Agenda.PacienteSemBaseLegal",
            description: "revogação aplicada na agenda");

        // Act
        await api.GrantAsync(patient);

        // Assert
        await api.BookWhenEligibleAsync(patient, Slot(32, 9));
    }

    [Fact]
    public async Task RevocationOfAnotherPurpose_DoesNotTouchScheduling()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        await api.GrantAsync(patient);
        await api.GrantAsync(patient, "notifications");
        var booked = await api.BookWhenEligibleAsync(patient, Slot(40, 9));

        // Act
        await api.RevokeAsync(patient, "notifications");

        // Assert: a revogação chegou à auditoria (logo, passou pelo broker) e a consulta continua marcada.
        await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => entries.Any(e => e.Action == "consent.revoked"),
            description: "revogação de notificações registrada");
        await Eventually.SucceedsAsync(async () => await CareFlowApi.InboxCountAsync(system.Scheduling, await LastConsentEventAsync(system, api.Clinic)) == 1);
        Assert.Equal("Booked", (await api.AppointmentAsync(booked)).Status);
        Assert.Equal(HttpStatusCode.Created, (await api.BookAsync(patient, Slot(41, 9))).Status);
    }

    private static async Task<Guid> LastConsentEventAsync(CareFlowSystem system, Guid clinic)
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
