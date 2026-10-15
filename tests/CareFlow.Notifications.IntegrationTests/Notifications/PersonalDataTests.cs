using CareFlow.Contracts.Appointments;
using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

using Npgsql;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

/// <summary>
/// Contato do paciente nunca em log nem no banco do Notifications; pseudônimo e clínica fora dos logs do
/// CareFlow. Percorre os fluxos com sucesso, fallback, falhas e bloqueio, com logs em nível Trace.
/// </summary>
public sealed class PersonalDataTests(NotificationsApiFixture api) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await api.ResetChannelsAsync();

    public async ValueTask DisposeAsync() => await api.ResetChannelsAsync();

    [Fact]
    public async Task AllFlows_LeaveNoContactInLogsOrDatabase_AndNoPseudonymInOwnLogs()
    {
        // Arrange
        var scenario = await Scenario.ReadyAsync(api);

        // Act: sucesso, fallback com resposta perdida e latência, cancelamento, todos os canais fora.
        var booked = await scenario.BookAsync();
        await api.DispatchAsync();
        api.InjectFaults((Channel.WhatsApp, FaultMode.LostResponse), (Channel.Sms, FaultMode.Latency));
        await api.DeliverAsync(scenario.Tenant, Cancelled(booked, AppointmentCancellationReasons.ConsentRevoked));
        await api.DispatchAsync();
        api.InjectFaults((Channel.WhatsApp, FaultMode.Unavailable), (Channel.Sms, FaultMode.Error), (Channel.Email, FaultMode.Unavailable));
        await scenario.BookAsync();
        await api.DispatchAsync();

        // Assert: logs
        var secrets = new[] { scenario.Contact.Phone!, scenario.Contact.Email! };
        var all = api.Logs.Entries.Select(entry => entry.ToString()).ToArray();
        Assert.NotEmpty(all);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(all, entry => entry.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        var own = api.Logs.Entries
            .Where(entry => entry.Category.StartsWith("CareFlow.", StringComparison.Ordinal))
            .Select(entry => entry.OwnContent)
            .ToArray();
        Assert.NotEmpty(own);
        foreach (var value in new[] { scenario.Patient.ToString(), scenario.Patient.ToString("N"), scenario.Tenant.ToString(), scenario.Tenant.ToString("N") })
        {
            Assert.DoesNotContain(own, entry => entry.Contains(value, StringComparison.OrdinalIgnoreCase));
        }

        // Assert: banco inteiro, lido como admin, em texto.
        var dump = await DumpDatabaseAsync();
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, dump, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<string> DumpDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(api.AdminConnectionString);
        await connection.OpenAsync(Ct);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public'", connection))
        await using (var reader = await list.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var dump = new System.Text.StringBuilder();
        foreach (var table in tables)
        {
            await using var rows = new NpgsqlCommand($"SELECT coalesce(string_agg(t::text, '|'), '') FROM \"{table}\" t", connection);
            dump.Append(await rows.ExecuteScalarAsync(Ct));
        }

        return dump.ToString();
    }
}
