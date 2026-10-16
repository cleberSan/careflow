extern alias demo;

using System.Net;
using System.Text.Json;

using CareFlow.EndToEndTests.Infrastructure;

using Npgsql;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// O backend do Painel (ADR-0010, ADR-0013) contra o sistema inteiro: sessão por token opaco, cada visitante na própria
/// clínica sintética e na própria partição de circuit breaker, limites de uso e limpeza no fim.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class PanelTests(CareFlowSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Session_IsAnOpaqueTokenInTheBody_NeverACookieOrAServiceToken()
    {
        // Arrange
        using var visitor = new DemoClient(system.Demo);

        // Act
        var response = await visitor.OpenRawAsync();
        var body = await response.Content.ReadAsStringAsync(Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("eyJ", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        var token = json.RootElement.GetProperty("token").GetString();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.StartsWith("isolated-", json.RootElement.GetProperty("partition").GetString(), StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        // O token só aparece na criação; a leitura da sessão não o devolve.
        using var read = new HttpRequestMessage(HttpMethod.Get, "demo/v1/session");
        read.Headers.Add("X-CareFlow-Demo", "1");
        read.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var again = await visitor.Http.SendAsync(read, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.DoesNotContain(token!, await again.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await visitor.EndAsync();
    }

    [Fact]
    public async Task Requests_WithoutTheHeader_FromAnotherOrigin_OrWithoutSession_AreRefused()
    {
        // Arrange
        using var plain = system.Demo.CreateClient(new() { BaseAddress = new Uri("https://localhost/") });
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "demo/v1/sessions");
        foreign.Headers.Add("X-CareFlow-Demo", "1");
        foreign.Headers.Add("Origin", "https://evil.example");
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "demo/v1/session");
        anonymous.Headers.Add("X-CareFlow-Demo", "1");

        // Act
        var withoutHeader = await plain.PostAsync("demo/v1/sessions", null, Ct);
        var fromForeignOrigin = await plain.SendAsync(foreign, Ct);
        var withoutSession = await plain.SendAsync(anonymous, Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, fromForeignOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutSession.StatusCode);
    }

    [Fact]
    public async Task TwoVisitors_AreIsolated_FaultsTracesAndDataStayInTheirOwnSession()
    {
        // Arrange
        using var alice = new DemoClient(system.Demo);
        using var bruno = new DemoClient(system.Demo);
        await alice.OpenAsync();
        await bruno.OpenAsync();

        // Act: Alice derruba o WhatsApp; os dois agendam.
        await alice.DropWhatsAppAsync();
        var aliceBooking = await alice.BookAsync();
        var brunoBooking = await bruno.BookAsync();
        var aliceNotice = await ConfirmationAsync(alice);
        var brunoNotice = await ConfirmationAsync(bruno);

        // Assert: o aviso de Alice saiu por SMS; o de Bruno, pelo WhatsApp de sempre.
        Assert.Equal("Sms", aliceNotice.SentVia);
        Assert.Equal("WhatsApp", brunoNotice.SentVia);
        Assert.NotEqual(alice.Session.Partition, bruno.Session.Partition);

        var aliceChannels = await alice.ChannelsAsync();
        var brunoChannels = await bruno.ChannelsAsync();
        Assert.Equal("Unavailable", aliceChannels.Channels.Single(c => c.Channel == "whatsapp").Fault);
        Assert.All(brunoChannels.Channels, c => Assert.Null(c.Fault));

        // Bruno não lê o trace de Alice, nem conhecendo o id; Alice lê o dela.
        Assert.Equal(HttpStatusCode.NotFound, (await bruno.TraceRawAsync(aliceBooking.TraceId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.TraceRawAsync(aliceBooking.TraceId)).StatusCode);

        // Cada um vê só o próprio paciente e a própria consulta.
        var alicePatient = await alice.PatientAsync();
        var brunoPatient = await bruno.PatientAsync();
        Assert.NotEqual(alicePatient.Vault.PatientRef, brunoPatient.Vault.PatientRef);
        Assert.Equal([aliceBooking.AppointmentId], alicePatient.Scheduling.Select(a => a.Id));
        Assert.Equal([brunoBooking.AppointmentId], brunoPatient.Scheduling.Select(a => a.Id));

        await alice.EndAsync();
        await bruno.EndAsync();
    }

    [Fact]
    public async Task SamePatient_IsFullOnlyInTheVault_PseudonymElsewhere_MaskedInLogs()
    {
        // Arrange
        using var visitor = new DemoClient(system.Demo);
        await visitor.OpenAsync();
        var booking = await visitor.BookAsync();
        await ConfirmationAsync(visitor);

        // Act
        var views = await visitor.PatientAsync();
        var logs = await Eventually.GetAsync(
            () => visitor.LogsAsync(booking.TraceId),
            list => list.Length > 0,
            description: "logs do trace da reserva");

        // Assert: completo no cofre; nos outros serviços, só o pseudônimo.
        Assert.StartsWith("Paciente Sintético", views.Vault.FullName, StringComparison.Ordinal);
        Assert.Equal(11, views.Vault.Cpf.Length);
        Assert.All(views.Scheduling, a => Assert.Equal(views.Vault.PatientRef, a.PatientRef));
        Assert.Equal(views.Vault.PatientRef, views.ConsentPatientRef);
        Assert.Contains(views.Consent, c => c.Purpose == "scheduling" && c.Status == "granted");
        Assert.Matches("^[0-9a-f]{4}…$", views.LogMask);

        // Nos logs exportados do trace, nem nome, nem CPF, nem o pseudônimo inteiro.
        Assert.All(logs, log =>
        {
            Assert.DoesNotContain(views.Vault.FullName, log.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(views.Vault.Cpf, log.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(views.Vault.PatientRef.ToString(), log.Message, StringComparison.OrdinalIgnoreCase);
        });

        await visitor.EndAsync();
    }

    [Fact]
    public async Task Booking_IsOneTraceAcrossServices_AndTheBrowserSeesOnlyAllowedAttributes()
    {
        // Arrange
        using var visitor = new DemoClient(system.Demo);
        await visitor.OpenAsync();

        // Act
        var booking = await visitor.BookAsync();
        await ConfirmationAsync(visitor);
        var trace = await Eventually.GetAsync(
            () => visitor.TraceAsync(booking.TraceId),
            t => t.Spans.Any(s => s.Name == "deliver notification"),
            description: "trace com o envio do aviso");

        // Assert: Demo, agenda, broker, Notifications e cofre no mesmo trace, cada span com o serviço dono.
        var services = trace.Spans.Select(s => s.Service).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(new HashSet<string>(["careflow-demo", "careflow-scheduling", "careflow-notifications", "careflow-patient-vault"]), services);
        Assert.Contains(trace.Spans, s => s.Name == "process appointment.booked" && s.Service == "careflow-notifications");
        Assert.All(trace.Spans.SelectMany(s => s.Attributes.Keys), key =>
            Assert.True(key.StartsWith("careflow.", StringComparison.Ordinal) || key.StartsWith("http.", StringComparison.Ordinal)
                        || key.StartsWith("messaging.", StringComparison.Ordinal) || key.StartsWith("db.system", StringComparison.Ordinal), key));
        Assert.DoesNotContain(trace.Spans.SelectMany(s => s.Attributes.Keys), key => key is "url.full" or "url.path" or "server.address" or "db.query.text");

        await visitor.EndAsync();
    }

    [Fact]
    public async Task RevokingConsent_CancelsAppointments_NotifiesThePatient_AndKeepsTheAuditChainIntact()
    {
        // Arrange
        using var visitor = new DemoClient(system.Demo);
        await visitor.OpenAsync();
        var first = await visitor.BookAsync();
        var second = await visitor.BookAsync();
        await Eventually.GetAsync(
            () => visitor.PatientAsync(),
            v => v.Notifications.Count(n => n.Kind == "Confirmation" && n.Status == "Sent") == 2,
            description: "duas confirmações enviadas");

        // Act
        var revoke = await visitor.RevokeAsync();
        var propagation = await Eventually.GetAsync(
            () => visitor.PropagationAsync(),
            p => p.Appointments.All(a => a.Status == "Cancelled")
                 && p.Notifications.Count(n => n.Kind == "Cancellation" && n.Status == "Sent") == 2
                 && p.Audit.Count(e => e.Action == "appointment.cancelled.consent-revoked") == 2,
            description: "revogação propagada até a auditoria");
        var trace = await Eventually.GetAsync(
            () => visitor.TraceAsync(revoke.TraceId),
            t => t.Spans.Select(s => s.Service).Distinct().Count() >= 5,
            description: "trace da revogação nos cinco serviços");

        // Assert
        Assert.Equal([first.AppointmentId, second.AppointmentId], propagation.Appointments.Select(a => a.Id).Order());
        Assert.All(propagation.Appointments, a => Assert.Equal("ConsentRevoked", a.CancellationReason));
        Assert.Contains(propagation.Audit, e => e.Action == "consent.revoked" && e.SourceService == "consent");
        Assert.True(propagation.Chain.Intact);
        Assert.Equal(propagation.Chain.LastSequence, propagation.Chain.EntriesVerified);
        Assert.Superset(
            new HashSet<string>(["careflow-demo", "careflow-consent", "careflow-scheduling", "careflow-notifications", "careflow-audit"]),
            trace.Spans.Select(s => s.Service).ToHashSet(StringComparer.Ordinal));

        await visitor.EndAsync();
    }

    [Fact]
    public async Task DroppingWhatsApp_FallsBackToSms_TheCircuitOpens_AndClosesAfterRestore()
    {
        // Arrange
        using var visitor = new DemoClient(system.Demo);
        await visitor.OpenAsync();

        // Act 1: WhatsApp fora; reservas até o circuito da sessão abrir.
        var dropped = await visitor.DropWhatsAppAsync();
        string circuit = "Closed";
        var bookings = 0;
        while (circuit != "Open" && bookings < 4)
        {
            await visitor.BookAsync();
            bookings++;
            await Eventually.GetAsync(
                () => visitor.PatientAsync(),
                views => views.Notifications.Count(n => n.Kind == "Confirmation" && n.Status == "Sent") == bookings,
                description: "confirmações enviadas");
            circuit = (await visitor.ChannelsAsync()).Channels.Single(c => c.Channel == "whatsapp").Circuit;
        }

        var duringOutage = (await visitor.PatientAsync()).Notifications.Where(n => n.Kind == "Confirmation").ToArray();

        // Act 2: WhatsApp de volta; depois da pausa do circuito, a próxima reserva sai pelo WhatsApp.
        await visitor.RestoreWhatsAppAsync();
        await Task.Delay(TimeSpan.FromSeconds(3.5), Ct);
        var recovered = await visitor.BookAsync();
        var notice = await Eventually.GetAsync(
            () => visitor.PatientAsync(),
            views => views.Notifications.Any(n => n.AppointmentId == recovered.AppointmentId && n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação depois da volta do WhatsApp");
        var after = await visitor.ChannelsAsync();

        // Assert
        Assert.Equal("Unavailable", dropped.Channels.Single(c => c.Channel == "whatsapp").Fault);
        Assert.Equal("Open", circuit);
        Assert.All(duringOutage, n => Assert.Equal("Sms", n.SentVia));
        Assert.All(duringOutage, n => Assert.Contains(n.Attempts, a => a.Channel == "WhatsApp" && a.Outcome != "Delivered"));
        Assert.Equal("WhatsApp", notice.Notifications.Single(n => n.AppointmentId == recovered.AppointmentId && n.Kind == "Confirmation").SentVia);
        Assert.Equal("Closed", after.Channels.Single(c => c.Channel == "whatsapp").Circuit);

        // O mundo compartilhado não foi tocado: o circuito dele não depende desta sessão.
        Assert.DoesNotContain("shared", after.Partition, StringComparison.Ordinal);

        await visitor.EndAsync();
    }

    [Fact]
    public async Task RightsRequests_ReadTheCodeInTheSimulatedInbox_AndStayInsideTheSession()
    {
        // Arrange
        using var visitor = new DemoClient(system.Demo);
        using var other = new DemoClient(system.Demo);
        await visitor.OpenAsync();
        await other.OpenAsync();

        // Act
        var opened = await visitor.OpenRightsRequestAsync("access");
        var inbox = await Eventually.GetAsync(() => visitor.InboxAsync(opened.Id), m => m.Length > 0, description: "código na caixa de entrada");
        var code = System.Text.RegularExpressions.Regex.Match(inbox[^1].Text, @"\b(\d{6})\b").Groups[1].Value;
        using var peek = await other.RightsRequestRawAsync(opened.Id);
        using var guess = await other.ConfirmIdentityRawAsync(opened.Id, code);
        using var invalid = await visitor.ConfirmIdentityRawAsync(opened.Id, "12ab");
        await visitor.ConfirmIdentityAsync(opened.Id, code);
        var done = await Eventually.GetAsync(() => visitor.RightsRequestAsync(opened.Id), r => r.Status == "completed", description: "pedido concluído");
        var package = await visitor.PackageAsync(opened.Id);

        // Assert
        Assert.Equal("received", opened.Status);
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, guess.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(6, done.Services.Count);
        Assert.Equal(["dados.json", "fhir-bundle.json", "leia-me.html"], package.Files.Select(f => f.Name).Order());
        Assert.Contains("Patient", package.FhirResources);
        Assert.True(package.ReuseRefused);

        await visitor.EndAsync();
        await other.EndAsync();
    }

    [Fact]
    public async Task RateLimits_CapNewSessionsPerClient_AndRequestsPerSession()
    {
        // Arrange: uma instância com limites baixos e partições próprias.
        await using var limited = system.StartDemo(
            ("Demo:FirstSlot", "9"),
            ("Demo:Slots", "4"),
            ("Demo:RateLimits:SessionsPerWindow", "2"),
            ("Demo:RateLimits:RequestsPerMinute", "5"));
        using var first = new DemoClient(limited);
        using var second = new DemoClient(limited);
        using var third = new DemoClient(limited);

        // Act
        await first.OpenAsync();
        await second.OpenAsync();
        var refused = await third.OpenRawAsync();
        var perSession = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            perSession.Add((await first.Http.GetAsync("demo/v1/session", Ct)).StatusCode);
        }

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.RetryAfter is not null);
        Assert.Equal(Enumerable.Repeat(HttpStatusCode.OK, 5).Append(HttpStatusCode.TooManyRequests), perSession);

        // A outra sessão tem o próprio balde.
        Assert.Equal(HttpStatusCode.OK, (await second.Http.GetAsync("demo/v1/session", Ct)).StatusCode);
    }

    [Fact]
    public async Task FullPool_RefusesNewSessions_UntilOneEnds()
    {
        // Arrange
        await using var single = system.StartDemo(("Demo:FirstSlot", "13"), ("Demo:Slots", "1"));
        using var first = new DemoClient(single);
        using var second = new DemoClient(single);
        await first.OpenAsync();

        // Act
        var full = await second.OpenRawAsync();
        await first.EndAsync();
        var reopened = await Eventually.GetAsync(() => second.OpenRawAsync(), r => r.StatusCode == HttpStatusCode.Created, description: "partição de volta ao pool");

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
        Assert.True(full.Headers.RetryAfter is not null);
        Assert.Equal(HttpStatusCode.Created, reopened.StatusCode);
        await second.EndAsync();
    }

    [Fact]
    public async Task EndedSession_StopsWorking_AndItsClinicIsPurged()
    {
        // Arrange
        using var visitor = new DemoClient(system.Demo);
        await visitor.OpenAsync();
        var booking = await visitor.BookAsync();
        await ConfirmationAsync(visitor);

        // Act
        await visitor.EndAsync();
        var afterEnd = await visitor.Http.GetAsync("demo/v1/session", Ct);

        // Assert: o token para de valer na hora; os dados somem depois da folga da limpeza.
        Assert.Equal(HttpStatusCode.Unauthorized, afterEnd.StatusCode);
        await Eventually.GetAsync(
            () => CountAsync(system.Scheduling, "SELECT count(*) FROM appointments WHERE id = @id", booking.AppointmentId),
            count => count == 0,
            description: "consulta apagada na agenda");
        Assert.Equal(0, await CountAsync(system.Vault, "SELECT count(*) FROM patients WHERE patient_ref = @id", visitor.Session.PatientRef));
        Assert.Equal(0, await CountAsync(system.Notifications, "SELECT count(*) FROM notifications WHERE appointment_id = @id", booking.AppointmentId));
    }

    private static async Task<demo::CareFlow.Demo.Api.Features.NotificationView> ConfirmationAsync(DemoClient visitor)
    {
        var views = await Eventually.GetAsync(
            () => visitor.PatientAsync(),
            v => v.Notifications.Any(n => n.Kind == "Confirmation" && n.Status == "Sent"),
            description: "confirmação enviada");
        return views.Notifications.First(n => n.Kind == "Confirmation" && n.Status == "Sent");
    }

    private static async Task<long> CountAsync(ServiceHost service, string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(service.AdminConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
