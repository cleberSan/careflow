using System.Net;

using CareFlow.EndToEndTests.Infrastructure;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Roda os fluxos que tocam dado pessoal (cadastro, leitura, busca por CPF, cadastro recusado, agendamento,
/// aviso com fallback, revogação, auditoria) e varre tudo o que foi exportado (spans, logs e métricas, depois dos
/// filtros de cada serviço) atrás do nome, CPF, telefone e e-mail do paciente sintético. Pseudônimo e clínica
/// também não aparecem: caminhos de URL saem como template e query strings são descartadas.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class TelemetryPersonalDataTests(CareFlowSystem system)
{
    [Fact]
    public async Task AllFlows_ExportNoPersonalData_InSpansLogsOrMetrics()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(40).AddHours(14);

        // Act: todos os caminhos que levam dado pessoal, inclusive os de erro.
        var patientRef = await api.RegisterPatientAsync();
        var patient = api.LastPatient;
        Assert.Equal(HttpStatusCode.Conflict, await api.RegisterDuplicateAsync());
        await api.ReadPatientAsync(patientRef);
        Assert.Equal(HttpStatusCode.OK, await api.LookupAsync(patient.Cpf));
        Assert.Equal(HttpStatusCode.NotFound, await api.LookupAsync(CareFlow.Testing.SyntheticPatients.Cpf()));
        await api.GrantAsync(patientRef);
        await api.GrantAsync(patientRef, "notifications");
        var appointment = await api.BookWhenEligibleAsync(patientRef, start);
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada pelo SMS");
        await api.RevokeAsync(patientRef);
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Cancellation" && n.Status == "Sent"),
            description: "aviso de cancelamento enviado");
        await api.AuditTrailAsync(patientRef);
        await api.VerifyChainAsync();
        system.Telemetry.FlushMetrics();

        // Assert
        var exported = system.Telemetry.Everything().ToArray();
        Assert.Contains(exported, item => item.Kind == "span");
        Assert.Contains(exported, item => item.Kind == "log");
        Assert.Contains(exported, item => item.Kind == "metric");

        var nameToken = patient.FullName.Split(' ')[^1];
        var phoneLocal = patient.Phone[4..];
        var forbidden = new (string What, string Value)[]
        {
            ("nome", patient.FullName),
            ("nome (parte única)", nameToken),
            ("CPF", patient.Cpf),
            ("CPF formatado", $"{patient.Cpf[..3]}.{patient.Cpf[3..6]}.{patient.Cpf[6..9]}-{patient.Cpf[9..]}"),
            ("e-mail", patient.Email),
            ("e-mail (usuário)", patient.Email.Split('@')[0]),
            ("telefone", patient.Phone),
            ("telefone sem DDI", phoneLocal),
            ("pseudônimo", patientRef.ToString()),
            ("pseudônimo (N)", patientRef.ToString("N")),
            ("clínica", api.Clinic.ToString()),
            ("clínica (N)", api.Clinic.ToString("N")),
        };

        foreach (var (what, value) in forbidden)
        {
            var leaks = exported.Where(item => item.Content.Contains(value, StringComparison.OrdinalIgnoreCase)).Take(3).ToArray();
            Assert.True(leaks.Length == 0, $"{what} apareceu na telemetria exportada: {string.Join(" || ", leaks.Select(l => $"[{l.Kind}] {l.Content}"))}");
        }
    }
}
