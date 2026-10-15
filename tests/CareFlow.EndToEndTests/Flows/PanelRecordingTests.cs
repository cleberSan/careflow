extern alias demo;

using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

using CareFlow.EndToEndTests.Infrastructure;

using demo::CareFlow.Demo.Api.Features;
using demo::CareFlow.Demo.Api.Telemetry;

using AppointmentView = demo::CareFlow.Demo.Api.Features.AppointmentView;
using NotificationView = demo::CareFlow.Demo.Api.Features.NotificationView;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Grava os quatro cenários do Painel contra o sistema inteiro, com um paciente sintético só, para o site
/// reproduzir (ADR-0010). Roda em todo CI como teste; o arquivo só é escrito quando
/// <c>CAREFLOW_PANEL_RECORDING</c> aponta para um caminho. Ordem: reserva, visões, queda do WhatsApp e, por
/// último, a revogação (que cancela tudo e impediria novas reservas).
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
    public async Task RecordsTheFourScenarios_WithoutPersonalDataOutsideTheVault()
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

        // Act 3: revogação e a propagação até a auditoria.
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
        await visitor.EndAsync();

        var recording = new PanelRecording(
            Schema: 1,
            RecordedAt: DateTimeOffset.UtcNow,
            Commit: Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("CAREFLOW_COMMIT") ?? "local",
            Source: "Testes de ponta a ponta do CareFlow (Testcontainers com PostgreSQL e RabbitMQ, provedores simulados)",
            Patient: new RecordedPatient(MaskedVault.From(views.Vault), session.PatientRef, views.LogMask),
            Booking: new BookingScenario(booking, bookingTrace, bookingLogs),
            Views: new ViewsScenario(views.Scheduling, views.ConsentPatientRef, views.Consent, views.Notifications, views.LogMask, bookingLogs),
            Resilience: new ResilienceScenario(steps, fallbackTrace),
            Revocation: new RevocationScenario(revoke, before, after, revokeTrace, revokeLogs));

        // Assert: o que vai para o navegador não tem dado pessoal fora da visão (já mascarada) do cofre.
        var full = JsonSerializer.Serialize(recording, Json);
        var withoutVault = JsonSerializer.Serialize(recording with { Patient = recording.Patient with { Vault = null } }, Json);
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

        if (Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, full, Ct);
        }
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
    RevocationScenario Revocation);

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
