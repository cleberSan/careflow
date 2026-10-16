extern alias demo;

using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

using CareFlow.EndToEndTests.Infrastructure;

using demo::CareFlow.Demo.Api.Features;
using demo::CareFlow.Demo.Api.Telemetry;

using AppointmentView = demo::CareFlow.Demo.Api.Features.AppointmentView;
using AuditEntryView = demo::CareFlow.Demo.Api.Features.AuditEntryView;
using NotificationView = demo::CareFlow.Demo.Api.Features.NotificationView;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Grava os seis cenários do Painel contra o sistema inteiro, com um paciente sintético só, para o site
/// reproduzir (ADR-0010). Roda em todo CI como teste; o arquivo só é escrito quando
/// <c>CAREFLOW_PANEL_RECORDING</c> aponta para um caminho. Ordem: reserva, visões, queda do WhatsApp, triagem com
/// IA (fase 7, com as respostas gravadas do modelo), a revogação (que cancela tudo e impediria novas reservas) e,
/// por último, os direitos do titular (fase 8): portabilidade com o pacote e a eliminação, que encerra o paciente.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class PanelRecordingTests(CareFlowSystem system)
{
    public const string OutputVariable = "CAREFLOW_PANEL_RECORDING";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RecordsTheSixScenarios_WithoutPersonalDataOutsideTheVault()
    {
        // Arrange: partição própria e espaço para as reservas do cenário de resiliência.
        await using var demo = system.StartDemo(("Demo:FirstSlot", "14"), ("Demo:Slots", "1"), ("Demo:MaxBookingsPerSession", "8"));
        using var visitor = new DemoClient(demo);
        var clock = Stopwatch.StartNew();
        var session = await visitor.OpenAsync();

        // Act 1: reserva e o trace distribuído.
        var booking = await visitor.BookAsync();
        var views = await Eventually.GetAsync(
            () => visitor.PatientAsync(),
            v => v.Notifications.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada");
        var bookingTrace = await TraceWithAsync(visitor, booking.TraceId, "deliver notification");
        var bookingLogs = await visitor.LogsAsync(booking.TraceId);

        // Act 2: WhatsApp fora, fallback para SMS, circuito abre; WhatsApp volta e o circuito fecha.
        var steps = new List<ResilienceStep> { Step(clock, "inicio", await visitor.ChannelsAsync()) };
        steps.Add(Step(clock, "whatsapp-fora", await visitor.DropWhatsAppAsync()));
        BookingView? fallback = null;
        var circuit = "Closed";
        for (var round = 0; round < 4 && circuit != "Open"; round++)
        {
            var during = await visitor.BookAsync();
            fallback ??= during;
            var notice = await ConfirmationOfAsync(visitor, during.AppointmentId);
            var channels = await visitor.ChannelsAsync();
            circuit = channels.Channels.Single(c => c.Channel == "whatsapp").Circuit;
            steps.Add(Step(clock, "reserva", channels, during, notice));
        }

        steps.Add(Step(clock, "whatsapp-de-volta", await visitor.RestoreWhatsAppAsync()));
        await Task.Delay(TimeSpan.FromSeconds(3.5), Ct);
        var recovered = await visitor.BookAsync();
        var recoveredNotice = await ConfirmationOfAsync(visitor, recovered.AppointmentId);
        steps.Add(Step(clock, "reserva", await visitor.ChannelsAsync(), recovered, recoveredNotice));
        var fallbackTrace = await TraceWithAsync(visitor, fallback!.TraceId, "deliver notification");

        // Act 3: triagem com IA. Os casos vêm do conjunto de avaliação, então o pedido mascarado é o mesmo que gerou
        // as gravações: com elas no repositório, o Painel mostra a resposta real do modelo.
        var triage = await TriageScenarioAsync(visitor, views.Vault);

        // Act 4: revogação e a propagação até a auditoria.
        var before = await visitor.PropagationAsync();
        var revoke = await visitor.RevokeAsync();
        var after = await Eventually.GetAsync(
            () => visitor.PropagationAsync(),
            p => p.Appointments.All(a => a.Status == "Cancelled")
                 && p.Notifications.Count(n => n.Kind == "Cancellation" && n.Status == "Sent") == p.Appointments.Count
                 && p.Audit.Count(e => e.Action == "appointment.cancelled.consent-revoked") == p.Appointments.Count,
            description: "revogação propagada");
        var revokeTrace = await Eventually.GetAsync(
            () => visitor.TraceAsync(revoke.TraceId),
            t => t.Spans.Select(s => s.Service).Distinct().Count() >= 5,
            description: "trace da revogação");
        var revokeLogs = await visitor.LogsAsync(revoke.TraceId);

        // Act 5: direitos do titular. Portabilidade (código, todos os serviços, pacote) e depois a eliminação.
        var rights = await RightsScenarioAsync(visitor);
        await visitor.EndAsync();

        var recording = new PanelRecording(
            Schema: 3,
            RecordedAt: DateTimeOffset.UtcNow,
            Commit: Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("CAREFLOW_COMMIT") ?? "local",
            Source: "Testes de ponta a ponta do CareFlow (Testcontainers com PostgreSQL e RabbitMQ, provedores simulados)",
            Patient: new RecordedPatient(MaskedVault.From(views.Vault), session.PatientRef, views.LogMask),
            Booking: new BookingScenario(booking, bookingTrace, bookingLogs),
            Views: new ViewsScenario(views.Scheduling, views.ConsentPatientRef, views.Consent, views.Notifications, views.LogMask, bookingLogs),
            Resilience: new ResilienceScenario(steps, fallbackTrace),
            Triage: triage,
            Revocation: new RevocationScenario(revoke, before, after, revokeTrace, revokeLogs),
            Rights: rights);

        // Assert: o que vai para o navegador não tem dado pessoal fora da visão (já mascarada) do cofre e do que o
        // paciente escreveu na queixa (nome inventado e e-mail em example.com, como no cofre).
        var full = JsonSerializer.Serialize(recording, Json);
        var withoutVault = JsonSerializer.Serialize(
            recording with
            {
                Patient = recording.Patient with { Vault = null },
                Triage = triage with { Steps = [.. triage.Steps.Select(s => s with { Complaint = string.Empty, Submitted = s.Submitted with { Complaint = string.Empty }, Reviewed = s.Reviewed with { Complaint = string.Empty } })] },
                Rights = rights with { Package = rights.Package with { PatientName = null } },
            },
            Json);
        Assert.DoesNotContain(views.Vault.Cpf, full, StringComparison.Ordinal);
        Assert.DoesNotContain(views.Vault.Phone!, full, StringComparison.Ordinal);
        foreach (var personal in new[] { views.Vault.FullName, views.Vault.Email!, views.Vault.Cpf, views.Vault.Phone! })
        {
            Assert.DoesNotContain(personal, withoutVault, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("Sms", steps.First(s => s.Notification is not null).Notification!.SentVia);
        Assert.Equal("Open", circuit);
        Assert.Equal("WhatsApp", recoveredNotice.SentVia);
        Assert.Equal("Closed", steps[^1].Channels.Channels.Single(c => c.Channel == "whatsapp").Circuit);
        Assert.Contains(fallbackTrace.Spans, s => s.Events.Any(e => e.Name == "fallback"));
        Assert.True(after.Chain.Intact);
        Assert.Equal(views.Vault.FullName, rights.Package.PatientName);
        Assert.False(rights.Evidence.VaultReadable);
        Assert.True(rights.Evidence.Chain.Intact);

        if (Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, full, Ct);
        }
    }

    /// <summary>
    /// Portabilidade e eliminação pelo Painel: o código chega na caixa de entrada simulada, o pedido segue para os
    /// seis serviços, o pacote é baixado pelo link de uso único e, na eliminação, o cofre deixa de ler o paciente
    /// com a cadeia da auditoria íntegra. O código gravado já foi usado e o pacote, destruído com a sessão.
    /// </summary>
    private static async Task<RightsScenario> RightsScenarioAsync(DemoClient visitor)
    {
        var portability = await RightsFlowAsync(visitor, "portability");
        var package = await visitor.PackageAsync(portability.Opened.Id);
        var erasure = await RightsFlowAsync(visitor, "erasure");
        var evidence = await Eventually.GetAsync(
            () => visitor.ErasureEvidenceAsync(),
            e => !e.VaultReadable && e.Audit.Any(a => a.Action == "patient.erased") && e.Audit.Any(a => a.Action == "subject-request.completed"),
            description: "evidência da eliminação");
        var trace = await Eventually.GetAsync(
            () => visitor.TraceAsync(erasure.Confirmed.TraceId!),
            t => t.Spans.Select(s => s.Service).Distinct().Count() >= 7,
            description: "trace da eliminação em todos os serviços");
        var logs = await visitor.LogsAsync(erasure.Confirmed.TraceId!);

        Assert.All([portability.Completed, erasure.Completed], r => Assert.Equal("completed", r.Status));
        Assert.Equal(["Appointment", "Consent", "Patient"], package.FhirResources.Distinct().Order());
        Assert.True(package.ReuseRefused);
        Assert.Equal("2026-10.1", erasure.Completed.Receipt!.PolicyVersion);
        return new RightsScenario(portability, package, erasure, evidence, trace, logs);
    }

    private static async Task<RightsFlow> RightsFlowAsync(DemoClient visitor, string kind)
    {
        var opened = await visitor.OpenRightsRequestAsync(kind);
        var inbox = await Eventually.GetAsync(() => visitor.InboxAsync(opened.Id), m => m.Length > 0, description: "código na caixa de entrada");
        var code = System.Text.RegularExpressions.Regex.Match(inbox[^1].Text, @"\b(\d{6})\b").Groups[1].Value;
        var confirmed = await visitor.ConfirmIdentityAsync(opened.Id, code);
        var completed = await Eventually.GetAsync(
            () => visitor.RightsRequestAsync(opened.Id),
            r => r.Status is "completed" or "denied",
            description: $"pedido de {kind} concluído");
        return new RightsFlow(opened, inbox, confirmed, completed);
    }

    /// <summary>
    /// Quatro triagens: dados pessoais mascarados antes do modelo, sinal de alarme que sobe a prioridade, tentativa de
    /// manipular a IA barrada antes do modelo e, depois da revogação do ai-triage, uma queixa que fica só com as
    /// regras. Toda sugestão termina com a decisão do profissional.
    /// </summary>
    private static async Task<TriageScenario> TriageScenarioAsync(DemoClient visitor, VaultView vault)
    {
        var cases = TriageCases.Load();
        var personal = cases["p09"];
        var masked = personal.Complaint
            .Replace(personal.FullName!, vault.FullName, StringComparison.Ordinal)
            .Replace("paciente.sintetico.7f3a@example.com", vault.Email!, StringComparison.Ordinal);
        var consentBefore = await visitor.AiConsentAsync();
        var steps = new List<TriageStep>
        {
            await TriageStepAsync(visitor, "dados-pessoais", personal, masked),
            await TriageStepAsync(visitor, "sinal-de-alarme", cases["a16"]),
            await TriageStepAsync(visitor, "tentativa-de-manipulacao", cases["i05"]),
        };

        var revocation = await visitor.RevokeAiTriageAsync();
        var consentAfter = await Eventually.GetAsync(() => visitor.AiConsentAsync(), c => c.State == "revoked", description: "revogação do ai-triage no Triage");
        steps.Add(await TriageStepAsync(visitor, "sem-consentimento", cases["c01"]));

        var trace = await TraceWithAsync(visitor, steps[0].Submitted.TraceId, "call triage model");
        var logs = await visitor.LogsAsync(steps[0].Submitted.TraceId);
        var propagation = await Eventually.GetAsync(
            () => visitor.PropagationAsync(),
            p => p.Audit.Count(e => e.SourceService == "triage" && e.Action.StartsWith("triage.suggested.", StringComparison.Ordinal)) == steps.Count
                 && p.Audit.Count(e => e.Action is "triage.confirmed" or "triage.corrected") == steps.Count,
            description: "triagens na trilha de auditoria");
        var audit = propagation.Audit.Where(e => e.SourceService == "triage").ToArray();

        // O resumo na trilha é o SHA-256 do pedido que saiu; triagens que não saíram não têm resumo.
        Assert.Equal("Meu nome é [nome], e-mail [email], estou com tosse seca há três dias e um pouco de cansaço.", steps[0].Submitted.SentToModel);
        Assert.Contains(audit, e => e.ContentDigest is not null && e.ContentDigest == steps[0].Submitted.PromptDigest);
        Assert.Equal("emergency", steps[1].Reviewed.FinalPriority);
        Assert.Contains("falta-de-ar", steps[1].Submitted.AlarmSigns);
        Assert.Equal(("skipped-injection", (string?)null), (steps[2].Submitted.ModelStatus, steps[2].Submitted.SentToModel));
        Assert.Equal(("skipped-no-consent", (string?)null), (steps[3].Submitted.ModelStatus, steps[3].Submitted.SentToModel));
        Assert.All(steps, s => Assert.NotEqual("pending", s.Reviewed.Decision));
        Assert.True(propagation.Chain.Intact);

        return new TriageScenario(
            Model: "Respostas gravadas do modelo (eval/triage/recordings); sem gravação, o modelo conta como indisponível e só as regras sugerem",
            steps,
            consentBefore,
            revocation,
            consentAfter,
            audit,
            propagation.Chain,
            trace,
            logs);
    }

    /// <summary>
    /// O profissional confirma quando a sugestão bate com a prioridade de referência do caso e corrige para ela
    /// quando não bate ou quando não houve sugestão (modelo indisponível ou barrado, sem sinal de alarme).
    /// </summary>
    private static async Task<TriageStep> TriageStepAsync(DemoClient visitor, string scenario, TriageCase @case, string? complaint = null)
    {
        complaint ??= @case.Complaint;
        var submitted = await visitor.SubmitTriageAsync(complaint);
        var reviewed = submitted.SuggestedPriority == @case.Expected
            ? await visitor.ReviewTriageAsync(submitted.Id, "confirm")
            : await visitor.ReviewTriageAsync(submitted.Id, "correct", @case.Expected);
        return new TriageStep(scenario, @case.Id, complaint, submitted, reviewed);
    }

    private static ResilienceStep Step(Stopwatch clock, string action, ChannelsView channels, BookingView? booking = null, NotificationView? notification = null) =>
        new((long)clock.Elapsed.TotalMilliseconds, action, channels, booking, notification);

    private static async Task<NotificationView> ConfirmationOfAsync(DemoClient visitor, Guid appointment)
    {
        var views = await Eventually.GetAsync(
            () => visitor.PatientAsync(),
            v => v.Notifications.Any(n => n.AppointmentId == appointment && n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação da reserva");
        return views.Notifications.Single(n => n.AppointmentId == appointment && n.Kind == "Confirmation");
    }

    private static Task<TraceView> TraceWithAsync(DemoClient visitor, string traceId, string span) =>
        Eventually.GetAsync(() => visitor.TraceAsync(traceId), t => t.Spans.Any(s => s.Name == span), description: $"trace com {span}");
}

internal sealed record PanelRecording(
    int Schema,
    DateTimeOffset RecordedAt,
    string Commit,
    string Source,
    RecordedPatient Patient,
    BookingScenario Booking,
    ViewsScenario Views,
    ResilienceScenario Resilience,
    TriageScenario Triage,
    RevocationScenario Revocation,
    RightsScenario Rights);

internal sealed record RecordedPatient(MaskedVault? Vault, Guid Pseudonym, string LogMask);

/// <summary>
/// A visão do cofre como o site publica: o CPF sintético passa na validação e poderia coincidir com um real, e
/// o telefone é de uma faixa comum; os dois saem com só os dois últimos dígitos. Nome e e-mail (example.com) são
/// inventados. Ao vivo, localmente, o Painel mostra tudo.
/// </summary>
internal sealed record MaskedVault(Guid PatientRef, string FullName, string Cpf, DateOnly BirthDate, string? Email, string? Phone)
{
    public static MaskedVault From(VaultView vault) =>
        new(vault.PatientRef, vault.FullName, "•••.•••.•••-" + vault.Cpf[^2..], vault.BirthDate, vault.Email, vault.Phone is { Length: > 2 } phone ? "+55 •• •••••-••" + phone[^2..] : null);
}

internal sealed record BookingScenario(BookingView Booking, TraceView Trace, IReadOnlyList<LogView> Logs);

internal sealed record ViewsScenario(
    IReadOnlyList<AppointmentView> Scheduling,
    Guid ConsentPatientRef,
    IReadOnlyList<ConsentView> Consent,
    IReadOnlyList<NotificationView> Notifications,
    string LogMask,
    IReadOnlyList<LogView> Logs);

internal sealed record ResilienceStep(long AtMs, string Action, ChannelsView Channels, BookingView? Booking, NotificationView? Notification);

internal sealed record ResilienceScenario(IReadOnlyList<ResilienceStep> Steps, TraceView FallbackTrace);

internal sealed record RevocationScenario(ActionView Action, PropagationView Before, PropagationView After, TraceView Trace, IReadOnlyList<LogView> Logs);

internal sealed record RightsFlow(
    RightsRequestView Opened,
    IReadOnlyList<InboxMessageView> Inbox,
    RightsRequestView Confirmed,
    RightsRequestView Completed);

internal sealed record RightsScenario(
    RightsFlow Portability,
    PackageContentsView Package,
    RightsFlow Erasure,
    ErasureEvidenceView Evidence,
    TraceView Trace,
    IReadOnlyList<LogView> Logs);

internal sealed record TriageStep(string Scenario, string Case, string Complaint, TriageView Submitted, TriageView Reviewed);

internal sealed record TriageScenario(
    string Model,
    IReadOnlyList<TriageStep> Steps,
    AiConsentView ConsentBefore,
    ActionView Revocation,
    AiConsentView ConsentAfter,
    IReadOnlyList<AuditEntryView> Audit,
    ChainView Chain,
    TraceView Trace,
    IReadOnlyList<LogView> Logs);

/// <summary>Os casos de <c>eval/triage/cases.json</c>, para o Painel usar exatamente as queixas que foram gravadas.</summary>
internal static class TriageCases
{
    public static IReadOnlyDictionary<string, TriageCase> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.TriageCases));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => new TriageCase(
                c.GetProperty("id").GetString()!,
                c.GetProperty("complaint").GetString()!,
                c.GetProperty("expected").GetString()!,
                c.TryGetProperty("fullName", out var name) ? name.GetString() : null))
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
    }
}

internal sealed record TriageCase(string Id, string Complaint, string Expected, string? FullName);
