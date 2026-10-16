using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;

using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Demo.Api.Common;
using CareFlow.Demo.Api.Services;
using CareFlow.Demo.Api.Sessions;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace CareFlow.Demo.Api.Features;

/// <summary>
/// Cenário de direitos do titular no Painel (ADR-0012): pedido, código na caixa de entrada simulada, andamento por
/// serviço, pacote baixado pelo link de uso único e, na eliminação, o cofre sem ler o paciente com a cadeia íntegra.
/// Só pedidos da própria sessão, sempre para o paciente sintético dela.
/// </summary>
internal static class RightsDemoEndpoints
{
    public static readonly Error RightsLimit = Error.Conflict("Demo.LimiteDePedidos", "Esta sessão já abriu o máximo de pedidos. Comece uma nova sessão.");

    public static readonly Error RequestNotFound = Error.NotFound("Demo.PedidoNaoEncontrado", "Pedido não encontrado nesta sessão.");

    public static readonly Error PackageUnavailable = Error.Conflict("Demo.PacoteIndisponivel", "O pacote ainda não está pronto ou já expirou.");

    public static RouteGroupBuilder MapRightsDemoEndpoints(this RouteGroupBuilder session)
    {
        session.MapPost("/rights/requests", OpenAsync);
        session.MapGet("/rights/requests/{id:guid}", GetAsync);
        session.MapGet("/rights/requests/{id:guid}/inbox", InboxAsync);
        session.MapPost("/rights/requests/{id:guid}/identity", ConfirmAsync);
        session.MapPost("/rights/requests/{id:guid}/package", PackageAsync);
        session.MapGet("/rights/erasure-evidence", ErasureEvidenceAsync);
        return session;
    }

    public static async Task<Results<Ok<RightsRequestView>, ProblemHttpResult>> OpenAsync(
        RightsRequestBody body,
        HttpContext http,
        CareFlowServices services,
        IOptions<DemoOptions> options,
        CancellationToken cancellationToken)
    {
        var session = http.Session();
        if (!session.TryReserveRightsRequest(options.Value.MaxRightsRequestsPerSession))
        {
            return RightsLimit.ToProblem();
        }

        using var activity = SessionLifecycle.ActivitySource.StartActivity("demo open subject request");
        var traceId = DemoEndpoints.Track(session);
        var outcome = await services.OpenRightsRequestAsync(session.Clinic, session.PatientRef, body?.Kind ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (outcome.Request is not { } request)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            return Refusal(outcome);
        }

        session.AddRightsRequest(request.Id);
        return TypedResults.Ok(View(request, traceId));
    }

    public static async Task<Results<Ok<RightsRequestView>, ProblemHttpResult>> GetAsync(
        Guid id, HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        if (!session.OwnsRightsRequest(id))
        {
            return RequestNotFound.ToProblem();
        }

        return TypedResults.Ok(View(await services.RightsRequestAsync(session.Clinic, id, cancellationToken).ConfigureAwait(false), null));
    }

    public static async Task<Results<Ok<IReadOnlyList<InboxMessageView>>, ProblemHttpResult>> InboxAsync(
        Guid id, HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        if (!session.OwnsRightsRequest(id))
        {
            return RequestNotFound.ToProblem();
        }

        var messages = await services.VerificationMessagesAsync(session.Clinic, id, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<InboxMessageView>>([.. messages.Select(m => new InboxMessageView(m.Channel, m.Text, m.AcceptedAt))]);
    }

    public static async Task<Results<Ok<RightsRequestView>, ProblemHttpResult>> ConfirmAsync(
        Guid id, IdentityCodeBody body, HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        if (!session.OwnsRightsRequest(id))
        {
            return RequestNotFound.ToProblem();
        }

        using var activity = SessionLifecycle.ActivitySource.StartActivity("demo confirm subject identity");
        var traceId = DemoEndpoints.Track(session);
        var outcome = await services.ConfirmIdentityAsync(session.Clinic, id, body?.Code, cancellationToken).ConfigureAwait(false);
        if (outcome.Request is not { } request)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            return Refusal(outcome);
        }

        return TypedResults.Ok(View(request, traceId));
    }

    /// <summary>Baixa o pacote como o titular faria e mostra o que veio nele. O ZIP não sai do Demo.</summary>
    public static async Task<Results<Ok<PackageContentsView>, ProblemHttpResult>> PackageAsync(
        Guid id, HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        if (!session.OwnsRightsRequest(id))
        {
            return RequestNotFound.ToProblem();
        }

        using var activity = SessionLifecycle.ActivitySource.StartActivity("demo download subject package");
        var traceId = DemoEndpoints.Track(session);
        var (zip, expiresAt, reuseRefused) = await services.DownloadPackageAsync(session.Clinic, id, cancellationToken).ConfigureAwait(false);
        if (zip is null)
        {
            return PackageUnavailable.ToProblem();
        }

        return TypedResults.Ok(Inspect(zip, expiresAt, reuseRefused, traceId));
    }

    public static async Task<Ok<ErasureEvidenceView>> ErasureEvidenceAsync(HttpContext http, CareFlowServices services, CancellationToken cancellationToken)
    {
        var session = http.Session();
        var status = await services.VaultReadStatusAsync(session.Clinic, session.PatientRef, cancellationToken).ConfigureAwait(false);
        var audit = await services.AuditTrailAsync(session.Clinic, session.PatientRef, cancellationToken).ConfigureAwait(false);
        var chain = await services.VerifyChainAsync(session.Clinic, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new ErasureEvidenceView(
            status == HttpStatusCode.OK,
            (int)status,
            [.. audit.Select(e => new AuditEntryView(e.Sequence, e.OccurredAt, e.SourceService, e.Action, e.ResourceType, e.ContentDigest))],
            new ChainView(chain.Intact, chain.EntriesVerified, chain.LastSequence)));
    }

    internal static PackageContentsView Inspect(byte[] zip, DateTimeOffset? expiresAt, bool reuseRefused, string traceId)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var files = archive.Entries.Select(e => new PackageFileView(e.FullName, e.Length)).ToList();

        var sections = new List<string>();
        string? patientName = null;
        if (archive.GetEntry("dados.json") is { } data)
        {
            using var stream = data.Open();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("secoes", out var secoes))
            {
                foreach (var section in secoes.EnumerateObject())
                {
                    sections.Add(section.Name);
                    if (section.Value.ValueKind == JsonValueKind.Object && section.Value.TryGetProperty("fullName", out var name))
                    {
                        patientName = name.GetString();
                    }
                }
            }
        }

        var resources = new List<string>();
        if (archive.GetEntry("fhir-bundle.json") is { } fhir)
        {
            using var stream = fhir.Open();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("entry", out var entries))
            {
                resources.AddRange(entries.EnumerateArray()
                    .Select(e => e.GetProperty("resource").GetProperty("resourceType").GetString() ?? "?"));
            }
        }

        return new PackageContentsView(files, sections, resources, patientName, expiresAt, reuseRefused, traceId);
    }

    private static RightsRequestView View(RightsRequest r, string? traceId) =>
        new(
            r.Id, r.Kind, r.Status, r.DenialReason, r.ReceivedAt, r.DueAt, r.Overdue, r.CodeSentAt, r.CodeChannel, r.IdentityConfirmedAt, r.CompletedAt,
            [.. r.Services.Select(s => new RightsServiceView(s.Service, s.Outcome, s.AnsweredAt))],
            r.Receipt is { } receipt
                ? new RightsReceiptView(receipt.PolicyVersion, receipt.ErasedAt, [.. receipt.Lines.Select(l => new RightsReceiptLineView(l.Service, l.Item, l.Description, l.Action, l.Records, l.RetainedUntil, l.Basis))])
                : null,
            r.Package is { } package ? new RightsPackageView(package.Available, package.Size, package.CreatedAt, package.ExpiresAt, package.DestroyedAt) : null,
            traceId);

    private static ProblemHttpResult Refusal(RightsOutcome outcome)
    {
        var error = outcome.Status switch
        {
            HttpStatusCode.Conflict => Error.Conflict(outcome.RefusalCode!, "O serviço de pedidos recusou a operação."),
            HttpStatusCode.NotFound => Error.NotFound(outcome.RefusalCode!, "Pedido não encontrado."),
            _ => Error.Validation(outcome.RefusalCode!, "O serviço de pedidos recusou os dados enviados."),
        };
        return error.ToProblem();
    }
}
