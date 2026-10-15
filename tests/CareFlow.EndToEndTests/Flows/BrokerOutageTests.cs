using System.Net;

using CareFlow.EndToEndTests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Broker fora: os serviços continuam atendendo, os eventos esperam no Outbox e são publicados quando o
/// broker volta. Nenhum evento se perde.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class BrokerOutageTests(CareFlowSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Slot(int day) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(day).AddHours(10);

    [Fact]
    public async Task EventsWrittenDuringOutage_AreDeliveredAfterRecovery()
    {
        // Arrange: paciente já conhecido pela agenda, ainda sem consentimento.
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        await Eventually.GetAsync(
            () => api.BookAsync(patient, Slot(50)),
            attempt => attempt.Code == "Agenda.PacienteSemBaseLegal",
            description: "cadastro propagado para a agenda");

        await system.Broker.StopAsync();
        try
        {
            // Act: a concessão é aceita com o broker fora; o evento fica no Outbox e a publicação falha.
            await api.GrantAsync(patient);
            await Eventually.SucceedsAsync(
                async () => await PendingWithFailuresAsync(api.Clinic) > 0,
                description: "Outbox registrando tentativas falhas");

            // Broker fora não tira o serviço de prontidão (o Outbox existe para isso), mas aparece como degradado.
            var ready = await system.Consent.Client(api.Clinic).GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal(HealthStatus.Degraded, await BrokerHealthAsync());
            var refused = await api.BookAsync(patient, Slot(51));
            Assert.Equal("Agenda.PacienteSemBaseLegal", refused.Code);
        }
        finally
        {
            await system.Broker.StartAsync();
        }

        // Assert: com o broker de volta, o despachante reenvia e a agenda aplica a concessão.
        await api.BookWhenEligibleAsync(patient, Slot(52));
        await Eventually.SucceedsAsync(async () => await BrokerHealthAsync() == HealthStatus.Healthy, description: "conexão com o broker recuperada");
        await Eventually.SucceedsAsync(
            async () => await PendingWithFailuresAsync(api.Clinic) == 0,
            description: "Outbox esvaziado depois da volta do broker");
        var trail = await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => entries.Any(e => e.Action == "consent.granted") && entries.Any(e => e.Action == "appointment.booked"),
            description: "acessos do período de indisponibilidade na trilha");
        Assert.Single(trail, e => e.Action == "consent.granted");
    }

    private async Task<HealthStatus> BrokerHealthAsync()
    {
        var health = system.Consent.Services.GetRequiredService<HealthCheckService>();
        var report = await health.CheckHealthAsync(check => check.Tags.Contains("messaging"), Ct);
        return report.Status;
    }

    private async Task<long> PendingWithFailuresAsync(Guid clinic)
    {
        await using var connection = new NpgsqlConnection(system.Consent.AdminConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM outbox_messages WHERE tenant_id = $1 AND dispatched_at IS NULL AND attempts > 0", connection)
        {
            Parameters = { new() { Value = clinic } },
        };
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
