using System.IO.Compression;
using System.Net;
using System.Text.Json;

using CareFlow.EndToEndTests.Infrastructure;

using Npgsql;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Direitos do titular de ponta a ponta (ADR-0012), com RabbitMQ real e os sete serviços: o código chega pelo
/// provedor simulado, cada serviço responde pelo Outbox, o pacote sai pelo link de uso único e a eliminação deixa o
/// cofre ilegível com a cadeia de auditoria íntegra, inclusive depois de restaurar um backup do cofre.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class RightsFlowTests(CareFlowSystem system)
{
    private static readonly string[] AllServices = ["patient-vault", "scheduling", "consent", "notifications", "triage", "audit"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Slot(int day, int hour) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(day).AddHours(hour);

    [Fact]
    public async Task Portability_DeliversEveryServiceSection_AsReadableStructuredAndFhir_OnASingleUseLink()
    {
        // Arrange: um paciente com dado em todos os serviços.
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await SeedPatientAsync(api, day: 50);

        // Act
        var opened = await api.OpenSubjectRequestAsync(patient, "portability");
        var request = await api.ConfirmAndWaitAsync(opened.Id);
        var link = await api.CreateDownloadLinkAsync(request.Id);
        using var first = await api.DownloadAsync(link.Token);
        var zip = await first.Content.ReadAsByteArrayAsync(Ct);
        using var second = await api.DownloadAsync(link.Token);

        // Assert: concluído com a resposta de todos os serviços, código pelo SMS (o WhatsApp está fora).
        Assert.Equal("completed", request.Status);
        Assert.Equal("sms", request.CodeChannel);
        Assert.Equal(AllServices.Order(), request.Services.Select(s => s.Service).Order());
        Assert.All(request.Services, s => Assert.Equal("exported", s.Outcome));
        Assert.True(request.Package is { Available: true });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/zip", first.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", first.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        Assert.Equal(["dados.json", "fhir-bundle.json", "leia-me.html"], archive.Entries.Select(e => e.FullName).Order());

        using var data = JsonDocument.Parse(archive.GetEntry("dados.json")!.Open());
        var sections = data.RootElement.GetProperty("secoes");
        Assert.Equal(AllServices.Order(), sections.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(api.LastPatient.FullName, sections.GetProperty("patient-vault").GetProperty("fullName").GetString());
        Assert.Equal(api.LastPatient.Cpf, sections.GetProperty("patient-vault").GetProperty("cpf").GetString());
        Assert.NotEmpty(sections.GetProperty("scheduling").GetProperty("appointments").EnumerateArray());
        Assert.NotEmpty(sections.GetProperty("consent").GetProperty("history").EnumerateArray());
        Assert.NotEmpty(sections.GetProperty("notifications").GetProperty("notifications").EnumerateArray());
        Assert.NotEmpty(sections.GetProperty("triage").GetProperty("triages").EnumerateArray());
        Assert.Contains(
            sections.GetProperty("audit").GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("action").GetString() == "appointment.booked");

        using var fhir = JsonDocument.Parse(archive.GetEntry("fhir-bundle.json")!.Open());
        Assert.Equal("Bundle", fhir.RootElement.GetProperty("resourceType").GetString());
        var resources = fhir.RootElement.GetProperty("entry").EnumerateArray()
            .Select(e => e.GetProperty("resource").GetProperty("resourceType").GetString())
            .ToArray();
        Assert.Contains("Patient", resources);
        Assert.Contains("Appointment", resources);
        Assert.Contains("Consent", resources);

        using var readme = new StreamReader(archive.GetEntry("leia-me.html")!.Open());
        Assert.Contains("default-src 'none'", await readme.ReadToEndAsync(Ct), StringComparison.Ordinal);

        // A trilha registra o pedido, a identidade, o link e o download, e a cadeia continua íntegra.
        var trail = await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => entries.Any(e => e.Action == "subject-request.package-downloaded"),
            description: "download do pacote na auditoria");
        foreach (var action in new[] { "subject-request.received", "subject-request.identity-confirmed", "subject-request.completed", "subject-request.download-link-created" })
        {
            Assert.Contains(trail, e => e.SourceService == "rights" && e.Action == action);
        }

        Assert.Contains(trail, e => e.Action == "subject-request.package-downloaded" && e.Actor == "titular-via-link");
        var chain = await api.VerifyChainAsync();
        Assert.True(chain.Intact, chain.Failure);
    }

    [Fact]
    public async Task Erasure_ShredsTheVault_KeepsTheChain_AndIsReappliedAfterABackupRestore()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await SeedPatientAsync(api, day: 52);
        var person = api.LastPatient;
        var future = await api.BookWhenEligibleAsync(patient, Slot(53, 11));
        var backup = await SnapshotVaultAsync(api.Clinic, patient);

        // Act
        var opened = await api.OpenSubjectRequestAsync(patient, "erasure");
        var request = await api.ConfirmAndWaitAsync(opened.Id);

        // Assert: concluído, com comprovante na versão da política e uma linha por item de cada serviço.
        Assert.Equal("completed", request.Status);
        Assert.All(request.Services, s => Assert.Equal("erased", s.Outcome));
        var receipt = Assert.IsType<ErasureReceiptView>(request.Receipt);
        Assert.Equal("2026-10.1", receipt.PolicyVersion);
        Assert.Equal(12, receipt.Lines.Length);
        Assert.Contains(receipt.Lines, l => l.Item == "vault.personal-data" && l.Action == "destroyed" && l.Records == 1);
        Assert.Contains(receipt.Lines, l => l.Item == "triage.text" && l.Action == "erased" && l.Records == 1);
        Assert.Contains(receipt.Lines, l => l.Item == "audit.trail" && l.Action == "retained");

        // O cofre não lê mais: a chave do paciente sumiu, o cifrado ficou (ilegível) e o CPF não acha o pseudônimo.
        Assert.Equal(HttpStatusCode.Gone, await api.PatientReadStatusAsync(patient));
        Assert.Equal(HttpStatusCode.NotFound, await api.LookupAsync(person.Cpf));
        var after = await SnapshotVaultAsync(api.Clinic, patient);
        Assert.Null(after.Key);
        Assert.Equal(backup.CipherText, after.CipherText);

        // A consulta futura foi cancelada pela eliminação.
        Assert.Equal("SubjectErasure", (await api.AppointmentAsync(future)).CancellationReason);

        // A trilha continua com o pseudônimo, ganha a eliminação de cada serviço e a cadeia fecha.
        var trail = await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => entries.Any(e => e.Action == "patient.erased") && entries.Any(e => e.Action == "subject-request.completed"),
            description: "eliminação na auditoria");
        Assert.Contains(trail, e => e.Action == "appointment.booked");
        Assert.Contains(trail, e => e.Action == "triage.text-erased");
        var chain = await api.VerifyChainAsync();
        Assert.True(chain.Intact, chain.Failure);

        // Act 2: o backup de antes da eliminação volta (a chave junto) e o cofre lê de novo...
        await RestoreVaultAsync(api.Clinic, backup);
        Assert.Equal(HttpStatusCode.OK, await api.PatientReadStatusAsync(patient));

        // ...até a eliminação ser reaplicada a partir dos pedidos concluídos.
        Assert.Equal(HttpStatusCode.Accepted, await api.ReplayErasureAsync(request.Id));
        await Eventually.GetAsync(
            () => api.PatientReadStatusAsync(patient),
            status => status == HttpStatusCode.Gone,
            description: "eliminação reaplicada no cofre");
        Assert.Null((await SnapshotVaultAsync(api.Clinic, patient)).Key);
        await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => entries.Any(e => e.Action == "patient.erasure-reapplied") && entries.Any(e => e.Action == "subject-request.erasure-reapplied"),
            description: "reaplicação na auditoria");
        var chainAfterReplay = await api.VerifyChainAsync();
        Assert.True(chainAfterReplay.Intact, chainAfterReplay.Failure);

        // Sem o índice cego, o mesmo CPF pode ser cadastrado de novo, com outro pseudônimo.
        Assert.Equal(HttpStatusCode.Created, await api.RegisterDuplicateAsync());
    }

    [Fact]
    public async Task Requests_StayInsideTheirClinic()
    {
        // Arrange: o mesmo pseudônimo pedido por outra clínica.
        var owner = new CareFlowApi(system, Guid.NewGuid());
        var stranger = new CareFlowApi(system, Guid.NewGuid());
        var patient = await owner.RegisterPatientAsync();
        var ownRequest = await owner.OpenSubjectRequestAsync(patient, "access");

        // Act
        var foreign = await stranger.OpenSubjectRequestAsync(patient, "erasure");
        var denied = await Eventually.GetAsync(
            () => stranger.SubjectRequestAsync(foreign.Id),
            r => r.Status == "denied",
            description: "pedido de outra clínica negado");
        using var peek = await stranger.SubjectRequestRawAsync(ownRequest.Id);

        // Assert: sem contato na clínica errada, nenhum código sai e nada é apagado na clínica dona.
        Assert.Equal("no-contact", denied.DenialReason);
        Assert.Empty(denied.Services);
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await owner.PatientReadStatusAsync(patient));
    }

    /// <summary>Cadastro, consentimentos, reserva com aviso enviado e uma triagem revisada.</summary>
    private static async Task<Guid> SeedPatientAsync(CareFlowApi api, int day)
    {
        var patient = await api.RegisterPatientAsync();
        await api.GrantAsync(patient);
        await api.GrantAsync(patient, "notifications");
        await api.GrantAsync(patient, "ai-triage");
        var appointment = await api.BookWhenEligibleAsync(patient, Slot(day, 10));
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada");
        var triage = await api.SubmitTriageAsync(patient, "Estou com tosse seca e cansaço há três dias, pior à noite, com dor no corpo e um pouco de dor de garganta.");
        await api.ReviewTriageAsync(triage.Id, "correct", triage.SuggestedPriority == "non-urgent" ? "less-urgent" : "non-urgent");
        await Eventually.GetAsync(
            () => api.AuditTrailAsync(patient),
            entries => entries.Any(e => e.Action == "appointment.booked"),
            description: "reserva na auditoria");
        return patient;
    }

    /// <summary>As linhas do paciente no cofre, como estariam num backup (admin, sem RLS).</summary>
    private async Task<VaultBackup> SnapshotVaultAsync(Guid clinic, Guid patient)
    {
        await using var connection = new NpgsqlConnection(system.Vault.AdminConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT row_to_json(p)::text, p.full_name_enc,
                   (SELECT row_to_json(k)::text FROM patient_keys k WHERE k.tenant_id = p.tenant_id AND k.patient_ref = p.patient_ref)
            FROM patients p WHERE p.tenant_id = $1 AND p.patient_ref = $2
            """,
            connection)
        {
            Parameters = { new() { Value = clinic }, new() { Value = patient } },
        };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct), "Paciente sumiu do cofre: o pseudônimo deveria ficar.");
        return new VaultBackup(reader.GetString(0), (byte[])reader[1], reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>Restaura as linhas do paciente como uma restauração de backup do banco faria.</summary>
    private async Task RestoreVaultAsync(Guid clinic, VaultBackup backup)
    {
        Assert.NotNull(backup.Key);
        await using var connection = new NpgsqlConnection(system.Vault.AdminConnectionString);
        await connection.OpenAsync(Ct);
        await using (var patient = new NpgsqlCommand(
            """
            UPDATE patients p
            SET cpf_index = s.cpf_index, erased_at = s.erased_at, updated_at = s.updated_at, key_scheme = s.key_scheme
            FROM json_populate_record(NULL::patients, $1::json) s
            WHERE p.tenant_id = $2 AND p.patient_ref = s.patient_ref
            """,
            connection)
        {
            Parameters = { new() { Value = backup.Patient }, new() { Value = clinic } },
        })
        {
            Assert.Equal(1, await patient.ExecuteNonQueryAsync(Ct));
        }

        await using var key = new NpgsqlCommand("INSERT INTO patient_keys SELECT * FROM json_populate_record(NULL::patient_keys, $1::json)", connection)
        {
            Parameters = { new() { Value = backup.Key } },
        };
        Assert.Equal(1, await key.ExecuteNonQueryAsync(Ct));
    }

    private sealed record VaultBackup(string Patient, byte[] CipherText, string? Key);
}
