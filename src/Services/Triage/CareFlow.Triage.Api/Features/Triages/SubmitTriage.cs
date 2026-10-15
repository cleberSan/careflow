using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Results;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Triage.Api.Common;
using CareFlow.Triage.Api.Common.Observability;
using CareFlow.Triage.Api.Features.Consents;
using CareFlow.Triage.Api.Infrastructure.Persistence;
using CareFlow.Triage.Api.Profiles;
using CareFlow.Triage.Domain.Cases;
using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.Domain.Text;

using Microsoft.AspNetCore.Http.HttpResults;

namespace CareFlow.Triage.Api.Features.Triages;

internal sealed partial class SubmitTriageHandler(
    TriageDbContext db,
    ITenantContext tenant,
    ITriageProfiles profiles,
    ITriageModel model,
    TimeProvider time,
    TriageMetrics metrics,
    ILogger<SubmitTriageHandler> logger)
{
    /// <summary>
    /// Avalia a queixa e grava a triagem pendente de revisão. Ordem: valida o texto, lê o consentimento local e o
    /// perfil no cofre (faixa etária e nome, só em memória), roda o motor (alarmes, mascaramento, modelo,
    /// proteções) e grava triagem e auditoria na mesma transação. A sugestão nunca vale sozinha.
    /// </summary>
    public async Task<Result<TriageCase>> HandleAsync(SubmitTriageRequest request, string actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PatientRef == Guid.Empty)
        {
            return TriageErrors.MissingIdentifier;
        }

        var complaint = request.Complaint is null ? null : TextFolding.Clean(request.Complaint);
        if (!TriageCase.IsComplaint(complaint))
        {
            return TriageErrors.InvalidComplaint;
        }

        var consented = await TriageConsentStore.AllowsAiAsync(db, request.PatientRef, cancellationToken).ConfigureAwait(false);
        var lookup = await profiles.FindAsync(tenant.TenantId, request.PatientRef, cancellationToken).ConfigureAwait(false);
        if (lookup.Status == TriageProfileStatus.NotFound)
        {
            return TriageErrors.PatientUnknown;
        }

        if (lookup.Profile is not { } profile)
        {
            return TriageErrors.VaultUnavailable;
        }

        using var activity = TriageDiagnostics.ActivitySource.StartActivity("assess triage");
        var assessment = await TriageEngine.AssessAsync(
            new TriageInput(complaint!, profile.AgeBand, profile.Identity, consented), model, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("careflow.triage.source", TriageMetrics.Slug(assessment.Source));
        activity?.SetTag("careflow.triage.model_status", assessment.ModelStatus.ToString());
        activity?.SetTag("careflow.triage.guardrails", assessment.Guardrails.Count);

        var now = time.GetUtcNow();
        var opened = TriageCase.Open(tenant.TenantId, request.PatientRef, complaint, assessment, profile.AgeBand.Name, now);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        var triage = opened.Value;
        db.Cases.Add(triage);
        db.RecordSuggestion(triage, actor, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        metrics.Assessed(assessment);
        LogAssessed(logger, triage.Id, assessment.Source, assessment.ModelStatus, assessment.Guardrails.Count);
        return triage;
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Triagem {TriageId} avaliada por {Source} (modelo {ModelStatus}, {GuardrailCount} proteções)")]
    private static partial void LogAssessed(ILogger logger, Guid triageId, SuggestionSource source, ModelStatus modelStatus, int guardrailCount);
}

internal static class SubmitTriageEndpoint
{
    public static async Task<Results<Created<TriageResponse>, ProblemHttpResult>> HandleAsync(
        SubmitTriageRequest request,
        SubmitTriageHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, AuditActor.From(httpContext.User), cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return TypedResults.Created($"/v1/triages/{result.Value.Id}", TriageResponse.From(result.Value));
        }

        if (result.Error == TriageErrors.VaultUnavailable)
        {
            // Falha de dependência, não do pedido: 503 com Retry-After, para o cliente tentar de novo.
            httpContext.Response.Headers.RetryAfter = "5";
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service Unavailable",
                detail: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
        }

        return result.Error.ToProblem();
    }
}
