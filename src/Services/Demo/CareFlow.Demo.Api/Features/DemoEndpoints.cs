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
            [.. audit.Select(e => new AuditEntryView(e.Sequence, e.OccurredAt, e.SourceService, e.Action, e.ResourceType))],
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
