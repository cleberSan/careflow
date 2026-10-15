using System.Diagnostics;
using System.Net;

using CareFlow.EndToEndTests.Infrastructure;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Uma reserva aparece como um trace só: da API da agenda, pelo Outbox e pelo RabbitMQ, até o aviso enviado pelo
/// Notifications (com a leitura de contato no cofre por HTTP no meio). A revogação também: do Consent à agenda,
/// que cancela, e de volta ao Notifications, que avisa o cancelamento.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class DistributedTraceTests(CareFlowSystem system)
{
    private static DateTimeOffset Slot(int day, int hour) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(day).AddHours(hour);

    [Fact]
    public async Task Booking_IsOneTrace_FromSchedulingApiToNotificationSentBySms()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        await api.GrantAsync(patient);
        await api.GrantAsync(patient, "notifications");

        // Act: cada tentativa leva um trace novo; o que vale é o da tentativa aceita.
        var (appointment, traceId) = await BookTracedAsync(api, patient, Slot(31, 10));
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada");
        var trace = await WaitForTraceAsync(traceId, spans => spans.Any(s => s.Name == "deliver notification" && s.Tag("careflow.notification.delivered_via") == "sms"));

        // Assert: os passos esperados, em ordem causal, no mesmo trace.
        var request = Single(trace, s => s.Kind == ActivityKind.Server && s.Tag("http.route")?.TrimEnd('/') == "/v1/appointments", "requisição da agenda");
        var publish = Single(trace, s => s.Name == "publish appointment.booked", "publicação do Outbox");
        var consume = Single(trace, s => s.Name == "process appointment.booked" && s.Tag("messaging.consumer.group.name") == "notifications", "consumo no Notifications");
        var delivery = Single(trace, s => s.Name == "deliver notification" && s.Tag("careflow.notification.delivered_via") == "sms", "envio do aviso");
        var contactCall = Single(trace, s => s.Kind == ActivityKind.Client && s.Source == "System.Net.Http" && s.ParentSpanId == delivery.SpanId, "chamada ao cofre");
        var contactServer = Single(trace, s => s.Kind == ActivityKind.Server && s.Tag("http.route") == "/v1/patients/{patientRef:guid}/contact", "cofre atendendo");

        Assert.Equal(request.SpanId, publish.ParentSpanId);
        Assert.Equal(publish.SpanId, consume.ParentSpanId);
        Assert.Equal(consume.SpanId, delivery.ParentSpanId);
        Assert.Equal(contactCall.SpanId, contactServer.ParentSpanId);

        // WhatsApp derrubado: a tentativa falha, o fallback vira evento e o SMS entrega.
        var whatsapp = trace.Where(s => s.Name == "send whatsapp" && s.ParentSpanId == delivery.SpanId).ToArray();
        Assert.NotEmpty(whatsapp);
        Assert.All(whatsapp, s => Assert.NotEqual("delivered", s.Tag("careflow.channel.result")));
        Assert.Contains(trace, s => s.Name == "send sms" && s.ParentSpanId == delivery.SpanId && s.Tag("careflow.channel.result") == "delivered");
        Assert.Contains(delivery.Events, e => e.StartsWith("fallback{from=whatsapp;to=sms", StringComparison.Ordinal));

        // Nada solto: todo span, menos a raiz do cliente, tem o pai dentro do trace.
        AssertConnected(trace);

        // Os logs exportados do envio carregam o mesmo trace.
        Assert.Contains(system.Telemetry.Logs, log => log.TraceId == traceId && log.Category.StartsWith("CareFlow.Notifications", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Revocation_IsOneTrace_FromConsentToCancellationNotice_AndMeasuresPropagation()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        await api.GrantAsync(patient);
        await api.GrantAsync(patient, "notifications");
        var appointment = await api.BookWhenEligibleAsync(patient, Slot(32, 11));
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada antes da revogação");

        // Act
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        api.ExtraHeaders["traceparent"] = $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01";
        await api.RevokeAsync(patient);
        api.ExtraHeaders.Remove("traceparent");
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Cancellation" && n.Status == "Sent"),
            description: "aviso de cancelamento enviado");
        var trace = await WaitForTraceAsync(traceId, spans => spans.Any(s => s.Name == "deliver notification" && s.Tag("careflow.notification.delivered_via") == "sms"));

        // Assert: Consent → agenda cancela → Notifications avisa, e a auditoria recebe tudo, num trace só.
        var revoke = Single(trace, s => s.Kind == ActivityKind.Server && s.Tag("http.route") == "/v1/patients/{patientRef:guid}/consents/{purpose}/revoke", "revogação no Consent");
        var atScheduling = Single(trace, s => s.Name == "process consent.revoked" && s.Tag("messaging.consumer.group.name") == "scheduling", "agenda recebe a revogação");
        var cancelled = Single(trace, s => s.Name == "publish appointment.cancelled", "agenda publica o cancelamento");
        var atNotifications = Single(trace, s => s.Name == "process appointment.cancelled" && s.Tag("messaging.consumer.group.name") == "notifications", "Notifications recebe o cancelamento");
        Assert.Contains(trace, s => s.Name == "process audit.data-access" && s.Tag("messaging.consumer.group.name") == "audit");
        Assert.True(revoke.StartedAt <= atScheduling.StartedAt && atScheduling.StartedAt <= cancelled.StartedAt && cancelled.StartedAt <= atNotifications.StartedAt);
        AssertConnected(trace);

        // O tempo de propagação da revogação até a agenda vira métrica, sem clínica nem pseudônimo nas tags.
        system.Telemetry.FlushMetrics();
        var lag = system.Telemetry.Metrics.Where(m => m.Name == "careflow.messaging.delivery.lag").ToArray();
        Assert.Contains(lag, m => m.Tags.Contains("event_type=consent.revoked;", StringComparison.Ordinal) && m.Tags.Contains("consumer=scheduling;", StringComparison.Ordinal));
        Assert.DoesNotContain(lag, m => m.Tags.Contains(patient.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IncomingBaggage_IsDropped_AndNeverReachesOtherServices()
    {
        // Arrange: um cliente manda baggage com um CPF; o padrão do .NET repassaria para frente.
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = await api.RegisterPatientAsync();
        await api.GrantAsync(patient);
        await api.GrantAsync(patient, "notifications");
        var cpf = api.LastPatient.Cpf;

        // Act
        api.ExtraHeaders["baggage"] = $"cpf={cpf},patient={patient}";
        var (appointment, bookedTrace) = await BookTracedAsync(api, patient, Slot(33, 9));
        api.ExtraHeaders.Remove("baggage");
        await Eventually.GetAsync(
            () => api.NotificationsAsync(appointment),
            list => list.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada");
        var trace = await WaitForTraceAsync(bookedTrace, spans => spans.Any(s => s.Name == "deliver notification" && s.Tag("careflow.notification.delivered_via") == "sms"));

        // Assert: nenhum span do trace (inclusive a chamada ao cofre) carrega o baggage.
        Assert.All(trace, span => Assert.DoesNotContain(cpf, span.Content, StringComparison.Ordinal));
        Assert.All(trace, span => Assert.DoesNotContain(patient.ToString(), span.Content, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(Guid Appointment, string TraceId)> BookTracedAsync(CareFlowApi api, Guid patient, DateTimeOffset start)
    {
        var key = Guid.NewGuid().ToString("N");
        string traceId = string.Empty;
        var attempt = await Eventually.GetAsync(
            async () =>
            {
                traceId = ActivityTraceId.CreateRandom().ToHexString();
                api.ExtraHeaders["traceparent"] = $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01";
                return await api.BookAsync(patient, start, key);
            },
            result => result.Status == HttpStatusCode.Created,
            description: "reserva aceita");
        api.ExtraHeaders.Remove("traceparent");
        return (attempt.AppointmentId!.Value, traceId);
    }

    private async Task<IReadOnlyList<ExportedSpan>> WaitForTraceAsync(string traceId, Func<IReadOnlyList<ExportedSpan>, bool> complete) =>
        await Eventually.GetAsync(
            () => Task.FromResult(system.Telemetry.Trace(traceId)),
            complete,
            description: $"trace {traceId} completo");

    private static ExportedSpan Single(IReadOnlyList<ExportedSpan> trace, Func<ExportedSpan, bool> predicate, string what)
    {
        var matches = trace.Where(predicate).ToArray();
        Assert.True(matches.Length == 1, $"Esperava um span de {what}, achei {matches.Length}. Trace: {string.Join(" / ", trace)}");
        return matches[0];
    }

    private static void AssertConnected(IReadOnlyList<ExportedSpan> trace)
    {
        var ids = trace.Select(s => s.SpanId).ToHashSet(StringComparer.Ordinal);
        var orphans = trace.Where(s => s.ParentSpanId is not null && !ids.Contains(s.ParentSpanId)).ToArray();

        // A raiz é a requisição do cliente de teste: o pai dela (o traceparent enviado) não é um span exportado.
        Assert.True(orphans.Length == 1, $"Spans sem pai no trace: {string.Join(" / ", orphans)}");
        Assert.Equal(ActivityKind.Server, orphans[0].Kind);
    }
}
