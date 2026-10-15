using CareFlow.EndToEndTests.Infrastructure;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>Depois de um fluxo completo pelos quatro serviços, nenhum log nosso carrega dado pessoal ou identificador da clínica.</summary>
[Collection(SequentialFlows.Name)]
public sealed class LogRedactionTests(CareFlowSystem system)
{
    [Fact]
    public async Task FullFlow_LogsCarryNoPersonalData()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(70).AddHours(9);

        // Act
        var patientRef = await api.RegisterPatientAsync();
        var patient = api.LastPatient;
        await api.GrantAsync(patientRef);
        await api.BookWhenEligibleAsync(patientRef, start);
        await api.RevokeAsync(patientRef);
        await Eventually.GetAsync(
            () => api.AuditTrailAsync(patientRef),
            entries => entries.Any(e => e.Action == "appointment.cancelled.consent-revoked"),
            description: "revogação processada");

        // Assert
        var own = system.Logs.Entries
            .Where(entry => entry.Category.StartsWith("CareFlow.", StringComparison.Ordinal))
            .Select(entry => entry.OwnContent)
            .ToArray();
        Assert.NotEmpty(own);
        var forbidden = new[]
        {
            patientRef.ToString(), patientRef.ToString("N"), api.Clinic.ToString(), api.Clinic.ToString("N"),
            patient.FullName, patient.Cpf, patient.Email, patient.Phone,
        };
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(own, entry => entry.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }
}
