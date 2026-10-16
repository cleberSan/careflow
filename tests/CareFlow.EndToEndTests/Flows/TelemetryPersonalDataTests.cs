using System.Net;

using CareFlow.EndToEndTests.Infrastructure;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Roda os fluxos que tocam dado pessoal (cadastro, leitura, busca por CPF, cadastro recusado, agendamento,
/// aviso com fallback, triagem com IA e revisão, revogação, pedidos do titular com código, pacote e eliminação,
/// auditoria) e varre tudo o que foi exportado (spans, logs e métricas, depois dos
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
        await api.GrantAsync(patientRef, "ai-triage");
        var appointment = await api.BookWhenEligibleAsync(patientRef, start);
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada pelo SMS");
        // A queixa traz todos os dados do paciente: só o banco do Triage guarda o texto, nada disso é exportado.
        var triage = await api.SubmitTriageAsync(
            patientRef,
            $"Sou {patient.FullName}, CPF {patient.Cpf}, e-mail {patient.Email}, telefone {patient.Phone}. Estou com tosse seca e cansaço há três dias, pior à noite, com dor no corpo e um pouco de dor de garganta quando acordo.");
        Assert.True(triage.SentToModel is not null, triage.ModelStatus);
        Assert.DoesNotContain(patient.FullName.Split(' ')[^1], triage.SentToModel, StringComparison.OrdinalIgnoreCase);
        await api.ReviewTriageAsync(triage.Id, "correct", triage.SuggestedPriority == "non-urgent" ? "less-urgent" : "non-urgent");
        await api.RevokeAsync(patientRef);
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Cancellation" && n.Status == "Sent"),
            description: "aviso de cancelamento enviado");

        // Pedidos do titular: código errado, código certo, pacote baixado (e o link recusado na volta), eliminação.
        var portability = await api.OpenSubjectRequestAsync(patientRef, "portability");
        var code = await api.VerificationCodeAsync(portability.Id);
        var wrongCode = code == "000000" ? "111111" : "000000";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.ConfirmIdentityRawAsync(portability.Id, wrongCode)).StatusCode);
        Assert.Equal("completed", (await api.ConfirmAndWaitAsync(portability.Id)).Status);
        var link = await api.CreateDownloadLinkAsync(portability.Id);
        Assert.Equal(HttpStatusCode.OK, (await api.DownloadAsync(link.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.DownloadAsync(link.Token)).StatusCode);
        var erasure = await api.OpenSubjectRequestAsync(patientRef, "erasure");
        var erasureCode = await api.VerificationCodeAsync(erasure.Id);
        Assert.Equal("completed", (await api.ConfirmAndWaitAsync(erasure.Id)).Status);
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
            ("queixa", "tosse seca e cansaço"),
            // O código sozinho (6 dígitos) aparece por acaso em ids hexadecimais; o texto da mensagem não.
            ("código de confirmação", $"é {code}"),
            ("código da eliminação", $"é {erasureCode}"),
            ("texto da mensagem do código", "seu código para confirmar"),
            ("token do link", link.Token),
            ("pedido", portability.Id.ToString()),
        };

        foreach (var (what, value) in forbidden)
        {
            var leaks = exported.Where(item => item.Content.Contains(value, StringComparison.OrdinalIgnoreCase)).Take(3).ToArray();
            Assert.True(leaks.Length == 0, $"{what} apareceu na telemetria exportada: {string.Join(" || ", leaks.Select(l => $"[{l.Kind}] {l.Content}"))}");
        }
    }
}
