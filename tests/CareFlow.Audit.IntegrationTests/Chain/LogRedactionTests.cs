using CareFlow.Audit.IntegrationTests.Infrastructure;

using Npgsql;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Chain;

/// <summary>
/// A trilha de quem acessou o quê mora só na tabela de auditoria. Nenhum log do CareFlow carrega o ator,
/// o pseudônimo do paciente ou a clínica, nem nos fluxos de duplicata e de cadeia adulterada.
/// </summary>
public sealed class LogRedactionTests(AuditApiFixture api)
{
    [Fact]
    public async Task AllFlows_LeaveNoActorOrPseudonymInLogs()
    {
        // Arrange
        var clinic = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var actor = $"ator-sintetico-{Guid.NewGuid():N}";
        var auditEvent = Domain.Chain.AuditEvent.Create(
            clinic, Guid.NewGuid(), DateTimeOffset.UtcNow, "patient-vault", actor, "patient.read", "patient", patient).Value;

        // Act
        await api.AppendAsync(auditEvent, Ct);
        await api.AppendAsync(auditEvent, Ct);
        await api.CreateClient(clinic, Read).ListOkAsync($"?resourceId={patient}");
        await api.CreateClient(clinic, Read).VerifyOkAsync();
        await using (var admin = new NpgsqlConnection(api.AdminConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SET session_replication_role = replica; UPDATE audit_entries SET action = 'x' WHERE tenant_id = @t", admin);
            command.Parameters.AddWithValue("t", clinic);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await api.CreateClient(clinic, Read).VerifyOkAsync();

        // Assert
        var own = api.Logs.Entries
            .Where(entry => entry.Category.StartsWith("CareFlow.", StringComparison.Ordinal))
            .Select(entry => entry.OwnContent)
            .ToArray();
        Assert.NotEmpty(own);
        foreach (var value in new[] { actor, patient.ToString(), patient.ToString("N"), clinic.ToString(), clinic.ToString("N") })
        {
            Assert.DoesNotContain(own, entry => entry.Contains(value, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(api.Logs.Entries, entry => entry.ToString().Contains(actor, StringComparison.Ordinal));
    }
}
