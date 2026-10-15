using System.Diagnostics;

using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Diagnostics;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Demo.Api.Common;
using CareFlow.Demo.Api.Services;
using CareFlow.Demo.Api.Sessions;
using CareFlow.Demo.Api.Telemetry;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace CareFlow.Demo.Api.Features;

/// <summary>
/// Rotas do Painel (ADR-0010). O navegador só fala com estas rotas, pela rota do site na mesma origem: nada de
/// token, endereço interno ou rota administrativa dos serviços chega a ele. Toda ação vale só para a clínica
/// sintética da sessão.
/// </summary>
internal static class DemoEndpoints
{
    public const string ProfessionalSeed = "c0ffee00-0000-4000-8000-000000000001";

    public static readonly Error BookingLimit = Error.Conflict("Demo.LimiteDeReservas", "Esta sessão já fez o máximo de reservas. Comece uma nova sessão.");

    public static readonly Error BookingRefused = Error.Conflict("Demo.ReservaRecusada", "A agenda recusou a reserva.");

    public static readonly Error TraceNotFound = Error.NotFound("Demo.TraceIndisponivel", "Trace não encontrado para esta sessão (ainda chegando ou fora do prazo de retenção).");

    public static readonly Error TriageLimit = Error.Conflict("Demo.LimiteDeTriagens", "Esta sessão já fez o máximo de triagens. Comece uma nova sessão.");

    public static readonly Error TriageNotFound = Error.NotFound("Demo.TriagemNaoEncontrada", "Triagem não encontrada nesta sessão.");

    private static readonly Guid Professional = Guid.Parse(ProfessionalSeed);

    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/demo/v1").WithTags("Demo").AddEndpointFilter<DemoHttp.RequestFilter>();

        demo.MapPost("/sessions", OpenSessionAsync).RequireRateLimiting(DemoHttp.SessionCreationPolicy);

        var session = demo.MapGroup(string.Empty)
            .AddEndpointFilter<DemoHttp.SessionFilter>()
            .RequireRateLimiting(DemoHttp.SessionPolicy);
        session.MapGet("/session", GetSession);
        session.MapDelete("/session", EndSession);
        session.MapPost("/appointments", BookAsync);
        session.MapGet("/patient", PatientAsync);
        session.MapPost("/consents/scheduling/revoke", RevokeAsync);
        session.MapPost("/consents/ai-triage/revoke", RevokeAiTriageAsync);
        session.MapGet("/triage-consent", AiConsentAsync);
        session.MapPost("/triages", SubmitTriageAsync);
        session.MapPost("/triages/{id:guid}/review", ReviewTriageAsync);
        session.MapGet("/propagation", PropagationAsync);
        session.MapGet("/channels", ChannelsAsync);
        session.MapPut("/channels/whatsapp/fault", DropWhatsAppAsync);
        session.MapDelete("/channels/whatsapp/fault", RestoreWhatsAppAsync);
        session.MapGet("/traces/{traceId}", TraceAsync);
        session.MapGet("/traces/{traceId}/logs", LogsAsync);
        return app;
    }

    public static async Task<Results<Created<SessionView>, ProblemHttpResult>> OpenSessionAsync(
        HttpContext http,
        SessionLifecycle lifecycle,
        IOptions<DemoOptions> options,
        CancellationToken cancellationToken)
    {
        var opened = await lifecycle.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            http.Response.Headers.RetryAfter = "60";
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: opened.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = opened.Error.Code });
        }

        var (session, cookie) = opened.Value;
        http.Response.Cookies.Append(DemoHttp.CookieName, cookie, DemoHttp.CookieOptions(session.ExpiresAt));
        return TypedResults.Created("/demo/v1/session", View(session, options.Value));
    }

    public static Ok<SessionView> GetSession(HttpContext http, IOptions<DemoOptions> options) =>
        TypedResults.Ok(View(http.Session(), options.Value));

    public static NoContent EndSession(HttpContext http, SessionLifecycle lifecycle)
    {
        lifecycle.End(http.Session());
        http.Response.Cookies.Delete(DemoHttp.CookieName, DemoHttp.CookieOptions(DateTimeOffset.UnixEpoch));
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Agenda uma consulta. Logo depois de a sessão abrir, a agenda pode ainda não conhecer o paciente (o cadastro
    /// e o consentimento chegam por eventos): o Demo repete com a mesma chave de idempotência, tudo no mesmo trace.
    /// </summary>
    public static async Task<Results<Ok<BookingView>, ProblemHttpResult>> BookAsync(
        HttpContext http,
        CareFlowServices services,
        IOptions<DemoOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var session = http.Session();
        if (!session.TryReserveBooking(options.Value.MaxBookingsPerSession, out var index))
        {
            return BookingLimit.ToProblem();
        }

        using var activity = SessionLifecycle.ActivitySource.StartActivity("demo book appointment");
        var traceId = Track(session);
        var startsAt = new DateTimeOffset(time.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero).AddDays(7).AddHours(12 + index);
        var key = Guid.NewGuid().ToString("N");
        var deadline = time.GetUtcNow() + options.Value.EligibilityWait;
        var refusals = new List<string>();
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await services.BookAsync(session.Clinic, session.PatientRef, Professional, startsAt, key, cancellationToken).ConfigureAwait(false);
            if (outcome.AppointmentId is { } id)
            {
                session.AddAppointment(id);
                return TypedResults.Ok(new BookingView(id, startsAt, traceId, attempt, refusals));
            }

            refusals.Add(outcome.RefusalCode!);
            if (time.GetUtcNow() >= deadline)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                return BookingRefused.ToProblem();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), time, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<Ok<PatientViews>> PatientAsync(HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        var vault = await services.PatientAsync(session.Clinic, session.PatientRef, cancellationToken).ConfigureAwait(false);
        var consents = await services.ConsentsAsync(session.Clinic, session.PatientRef, cancellationToken).ConfigureAwait(false);
        var appointments = await AppointmentsAsync(services, session, cancellationToken).ConfigureAwait(false);
        var notifications = await NotificationsAsync(services, session, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new PatientViews(
            new VaultView(vault.PatientRef, vault.FullName, vault.Cpf, vault.BirthDate, vault.Email, vault.Phone),
            appointments,
            session.PatientRef,
            [.. consents.Select(c => new ConsentView(c.Purpose, c.LegalBasis, c.Status, c.GrantedAt, c.RevokedAt))],
            notifications,
            LogMask.Pseudonym(session.PatientRef)));
    }

    public static async Task<Ok<ActionView>> RevokeAsync(HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        using var activity = SessionLifecycle.ActivitySource.StartActivity("demo revoke consent");
        var traceId = Track(session);
        await services.RevokeAsync(session.Clinic, session.PatientRef, "scheduling", cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new ActionView(traceId));
    }

    /// <summary>Revoga só a triagem com IA: as próximas queixas não saem da clínica, a agenda segue igual.</summary>
    public static async Task<Ok<ActionView>> RevokeAiTriageAsync(HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        using var activity = SessionLifecycle.ActivitySource.StartActivity("demo revoke ai-triage consent");
        var traceId = Track(session);
        await services.RevokeAsync(session.Clinic, session.PatientRef, "ai-triage", cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new ActionView(traceId));
    }

    public static async Task<Ok<AiConsentView>> AiConsentAsync(HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        var consent = await services.AiConsentAsync(session.Clinic, session.PatientRef, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new AiConsentView(consent.State, consent.Version));
    }

    /// <summary>
    /// Envia a queixa para a triagem em nome do paciente da sessão. Logo depois de a sessão abrir, o Triage pode
    /// ainda não conhecer o consentimento (ele chega por evento): o Demo espera a projeção sair de <c>unknown</c>,
    /// como faz com a agenda, para a primeira triagem não ficar sem IA só por ter chegado antes do evento. Se o
    /// prazo vencer, envia assim mesmo e o Triage segue só com as regras, o que o Painel mostra.
    /// </summary>
    public static async Task<Results<Ok<TriageView>, ProblemHttpResult>> SubmitTriageAsync(
        TriageRequestBody body,
        HttpContext http,
        CareFlowServices services,
        IOptions<DemoOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var session = http.Session();
        if (!session.TryReserveTriage(options.Value.MaxTriagesPerSession))
        {
            return TriageLimit.ToProblem();
        }

        using var activity = SessionLifecycle.ActivitySource.StartActivity("demo submit triage");
        var traceId = Track(session);
        var deadline = time.GetUtcNow() + options.Value.EligibilityWait;
        while ((await services.AiConsentAsync(session.Clinic, session.PatientRef, cancellationToken).ConfigureAwait(false)).State == "unknown"
               && time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, cancellationToken).ConfigureAwait(false);
        }

        var outcome = await services.SubmitTriageAsync(session.Clinic, session.PatientRef, body?.Complaint, cancellationToken).ConfigureAwait(false);
        if (outcome.Triage is not { } triage)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            return Refusal(outcome);
        }

        session.AddTriage(triage.Id);
        return TypedResults.Ok(View(triage, traceId));
    }

    /// <summary>O profissional confirma ou corrige; só triagens criadas pela própria sessão.</summary>
    public static async Task<Results<Ok<TriageView>, ProblemHttpResult>> ReviewTriageAsync(
        Guid id,
        TriageReviewBody body,
        HttpContext http,
        CareFlowServices services,
        CancellationToken cancellationToken)
    {
        var session = http.Session();
        if (!session.OwnsTriage(id))
        {
            return TriageNotFound.ToProblem();
        }

        using var activity = SessionLifecycle.ActivitySource.StartActivity("demo review triage");
        var traceId = Track(session);
        var outcome = await services.ReviewTriageAsync(session.Clinic, id, body?.Decision, body?.Priority, cancellationToken).ConfigureAwait(false);
        if (outcome.Triage is not { } triage)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            return Refusal(outcome);
        }

        return TypedResults.Ok(View(triage, traceId));
    }

    public static async Task<Ok<PropagationView>> PropagationAsync(HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        var appointments = await AppointmentsAsync(services, session, cancellationToken).ConfigureAwait(false);
        var notifications = await NotificationsAsync(services, session, cancellationToken).ConfigureAwait(false);
        var audit = await services.AuditTrailAsync(session.Clinic, session.PatientRef, cancellationToken).ConfigureAwait(false);
        var chain = await services.VerifyChainAsync(session.Clinic, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new PropagationView(
            appointments,
            notifications,
            [.. audit.Select(e => new AuditEntryView(e.Sequence, e.OccurredAt, e.SourceService, e.Action, e.ResourceType, e.ContentDigest))],
            new ChainView(chain.Intact, chain.EntriesVerified, chain.LastSequence)));
    }

    public static async Task<Ok<ChannelsView>> ChannelsAsync(HttpContext http, CareFlowServices services, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ChannelsOfAsync(services, http.Session(), cancellationToken).ConfigureAwait(false));

    /// <summary>Derruba o WhatsApp só para a clínica da sessão, com prazo: se o visitante sair, a falha vence sozinha.</summary>
    public static async Task<Ok<ChannelsView>> DropWhatsAppAsync(
        HttpContext http,
        CareFlowServices services,
        IOptions<DemoOptions> options,
        CancellationToken cancellationToken)
    {
        var session = http.Session();
        await services.SetFaultAsync(session.Clinic, "whatsapp", "Unavailable", options.Value.WhatsAppFaultDuration, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(await ChannelsOfAsync(services, session, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<Ok<ChannelsView>> RestoreWhatsAppAsync(HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        await services.ClearFaultAsync(session.Clinic, "whatsapp", cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(await ChannelsOfAsync(services, session, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<Results<Ok<TraceView>, ProblemHttpResult>> TraceAsync(
        string traceId,
        HttpContext http,
        ITelemetryReader telemetry,
        CancellationToken cancellationToken)
    {
        // Só traces que a própria sessão criou: o id vem do navegador e não pode abrir o trace de outra sessão.
        if (!TelemetryAllowList.IsValidTraceId(traceId) || !http.Session().OwnsTrace(traceId))
        {
            return TraceNotFound.ToProblem();
        }

        return await telemetry.TraceAsync(traceId, cancellationToken).ConfigureAwait(false) is { } trace
            ? TypedResults.Ok(trace)
            : TraceNotFound.ToProblem();
    }

    public static async Task<Results<Ok<IReadOnlyList<LogView>>, ProblemHttpResult>> LogsAsync(
        string traceId,
        HttpContext http,
        ITelemetryReader telemetry,
        CancellationToken cancellationToken)
    {
        if (!TelemetryAllowList.IsValidTraceId(traceId) || !http.Session().OwnsTrace(traceId))
        {
            return TraceNotFound.ToProblem();
        }

        return TypedResults.Ok(await telemetry.LogsAsync(traceId, cancellationToken).ConfigureAwait(false));
    }

    private static SessionView View(DemoSession session, DemoOptions options) =>
        new(session.ExpiresAt, $"isolated-{session.Slot:00}", session.PatientRef, session.Appointments.Count, options.MaxBookingsPerSession);

    private static TriageView View(TriageRecord t, string traceId) =>
        new(t.Id, t.Complaint, t.AgeBand, t.SentToModel, t.PromptDigest, t.PromptVersion, t.ModelName, t.ModelStatus, t.ModelPriority,
            t.SuggestedPriority, t.Source, t.Rationale, t.Guardrails, t.AlarmSigns, t.Masks, t.Decision, t.FinalPriority, t.ReviewedAt, traceId);

    /// <summary>Recusa do Triage com o código dele (queixa inválida, revisão repetida, limite da clínica).</summary>
    private static ProblemHttpResult Refusal(TriageOutcome outcome)
    {
        if (outcome.Status == System.Net.HttpStatusCode.TooManyRequests)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Muitas triagens agora. Tente de novo em instantes.", extensions: new Dictionary<string, object?> { ["code"] = outcome.RefusalCode });
        }

        var error = outcome.Status switch
        {
            System.Net.HttpStatusCode.Conflict => Error.Conflict(outcome.RefusalCode!, "A triagem recusou a revisão."),
            System.Net.HttpStatusCode.NotFound => Error.NotFound(outcome.RefusalCode!, "A triagem não encontrou o paciente ou a triagem."),
            _ => Error.Validation(outcome.RefusalCode!, "A triagem recusou o pedido."),
        };
        return error.ToProblem();
    }

    private static string Track(DemoSession session)
    {
        var traceId = Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        session.AddTrace(traceId);
        return traceId;
    }

    private static async Task<IReadOnlyList<AppointmentView>> AppointmentsAsync(CareFlowServices services, DemoSession session, CancellationToken cancellationToken)
    {
        var views = new List<AppointmentView>();
        foreach (var id in session.Appointments)
        {
            var appointment = await services.AppointmentAsync(session.Clinic, id, cancellationToken).ConfigureAwait(false);
            views.Add(new AppointmentView(appointment.Id, appointment.PatientRef, appointment.StartsAt, appointment.Status, appointment.CancellationReason));
        }

        return views;
    }

    private static async Task<IReadOnlyList<NotificationView>> NotificationsAsync(CareFlowServices services, DemoSession session, CancellationToken cancellationToken)
    {
        var views = new List<NotificationView>();
        foreach (var id in session.Appointments)
        {
            foreach (var notification in await services.NotificationsAsync(session.Clinic, id, cancellationToken).ConfigureAwait(false))
            {
                views.Add(new NotificationView(
                    id,
                    notification.Kind,
                    notification.Status,
                    notification.SentVia,
                    notification.NotSentReason,
                    [.. notification.Attempts.Select(a => new AttemptView(a.Round, a.Channel, a.Outcome))]));
            }
        }

        return views;
    }

    private static async Task<ChannelsView> ChannelsOfAsync(CareFlowServices services, DemoSession session, CancellationToken cancellationToken)
    {
        var channels = await services.ChannelsAsync(session.Clinic, cancellationToken).ConfigureAwait(false);
        return new ChannelsView(channels.Partition, [.. channels.Channels.Select(c => new ChannelView(c.Channel, c.Fault, c.ExpiresAt, c.Circuit))]);
    }
}
