using System.Net;
using System.Net.Http.Json;

using CareFlow.EndToEndTests.Infrastructure;

using Npgsql;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Limpeza do Painel (ADR-0010): quando a sessão acaba, o serviço Demo apaga a clínica sintética dela em cada
/// serviço. Some tudo da clínica (inclusive Outbox, Inbox e projeções); outra clínica não perde nada; a trilha de
/// auditoria fica, porque é só de inserção.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class DemoPurgeTests(CareFlowSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Slot(int day, int hour) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(day).AddHours(hour);

    private IEnumerable<ServiceHost> Purgeable => [system.Vault, system.Consent, system.Scheduling, system.Notifications];

    [Fact]
    public async Task Purge_RemovesEveryRowOfTheSessionClinic_AndNothingElse()
    {
        // Arrange: duas clínicas com o fluxo completo (cadastro, consentimentos, reserva e aviso).
        var visitor = await SeedClinicAsync(day: 40);
        var other = await SeedClinicAsync(day: 41);
        var auditBefore = await CountRowsAsync(system.Audit, visitor.Clinic);
        Assert.All(Purgeable, service => Assert.True(CountRowsAsync(service, visitor.Clinic).Result > 0, $"{service.Spec.ServiceName} sem dados"));

        // Act
        var purged = new List<int>();
        foreach (var service in Purgeable)
        {
            var response = await service.Client(visitor.Clinic, "demo.purge").DeleteAsync("/v1/demo/clinic-data", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            purged.Add((await response.Content.ReadFromJsonAsync<PurgeView>(Ct))!.Rows);
        }

        // Assert
        Assert.All(purged, rows => Assert.True(rows > 0));
        foreach (var service in Purgeable)
        {
            Assert.Equal(0, await CountRowsAsync(service, visitor.Clinic));
            Assert.True(await CountRowsAsync(service, other.Clinic) > 0, $"{service.Spec.ServiceName} perdeu dados de outra clínica");
        }

        Assert.Equal(auditBefore, await CountRowsAsync(system.Audit, visitor.Clinic));
        Assert.True((await other.VerifyChainAsync()).Intact);
        Assert.Equal("Booked", (await other.AppointmentAsync(other.Appointment)).Status);
    }

    [Fact]
    public async Task Purge_RequiresItsOwnScope_AndDoesNotExistInAudit()
    {
        // Arrange
        var clinic = Guid.NewGuid();

        // Act
        var withoutScope = await system.Scheduling.Client(clinic, "scheduling.write").DeleteAsync("/v1/demo/clinic-data", Ct);
        var audit = await system.Audit.Client(clinic, "demo.purge", "audit.read").DeleteAsync("/v1/demo/clinic-data", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, withoutScope.StatusCode);
        Assert.True(audit.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"Audit respondeu {audit.StatusCode}");
    }

    private async Task<SeededClinic> SeedClinicAsync(int day)
    {
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        await api.GrantAsync(patient);
        await api.GrantAsync(patient, "notifications");
        var appointment = await api.BookWhenEligibleAsync(patient, Slot(day, 10));
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada");
        await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => entries.Any(e => e.Action == "appointment.booked"),
            description: "reserva na auditoria");
        return new SeededClinic(api, appointment);
    }

    /// <summary>Linhas da clínica em todas as tabelas com <c>tenant_id</c> do banco do serviço (admin, sem RLS).</summary>
    private static async Task<long> CountRowsAsync(ServiceHost service, Guid clinic)
    {
        await using var connection = new NpgsqlConnection(service.AdminConnectionString);
        await connection.OpenAsync(Ct);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.columns WHERE table_schema = 'public' AND column_name = 'tenant_id'",
            connection))
        await using (var reader = await list.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        long total = 0;
        foreach (var table in tables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\" WHERE tenant_id = @tenant", connection);
            count.Parameters.AddWithValue("tenant", clinic);
            total += (long)(await count.ExecuteScalarAsync(Ct))!;
        }

        return total;
    }

    private sealed record SeededClinic(CareFlowApi Api, Guid Appointment)
    {
        public Guid Clinic => Api.Clinic;

        public Task<ChainVerificationView> VerifyChainAsync() => Api.VerifyChainAsync();

        public Task<AppointmentView> AppointmentAsync(Guid id) => Api.AppointmentAsync(id);
    }

    private sealed record PurgeView(int Rows);
}
