using CareFlow.Consent.IntegrationTests.Infrastructure;

using static CareFlow.Consent.IntegrationTests.Infrastructure.ConsentClient;

namespace CareFlow.Consent.IntegrationTests.Consents;

/// <summary>Nenhum log do CareFlow carrega o pseudônimo do paciente ou a clínica, em nenhum fluxo do Consent.</summary>
public sealed class LogRedactionTests(ConsentApiFixture api)
{
    [Fact]
    public async Task AllFlows_LeaveNoPseudonymOrClinicInLogs()
    {
        // Arrange
        var clinic = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var client = api.CreateClient(clinic, Read, Write);

        // Act
        await client.GrantAsync(patient, "scheduling", Specific());
        await client.GrantAsync(patient, "scheduling", Specific());
        await client.GrantAsync(patient, "ai-triage", new("health-protection", "2026-10"));
        await client.GrantAsync(patient, "ai-triage", Specific());
        await client.RevokeAsync(patient, "ai-triage");
        await client.GrantAsync(patient, "notifications", new("health-protection", "2026-10"));
        await client.RevokeAsync(patient, "scheduling");
        await client.RevokeAsync(patient, "notifications");
        await client.GrantAsync(patient, "scheduling", new("x", "2026-10"));
        await client.ListOkAsync(patient);

        // Assert
        var own = api.Logs.Entries
            .Where(entry => entry.Category.StartsWith("CareFlow.", StringComparison.Ordinal)
                && !entry.Category.StartsWith("CareFlow.Testing.", StringComparison.Ordinal))
            .Select(entry => entry.OwnContent)
            .ToArray();
        Assert.NotEmpty(own);
        foreach (var value in new[] { patient.ToString(), patient.ToString("N"), clinic.ToString(), clinic.ToString("N") })
        {
            Assert.DoesNotContain(own, entry => entry.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }
}
