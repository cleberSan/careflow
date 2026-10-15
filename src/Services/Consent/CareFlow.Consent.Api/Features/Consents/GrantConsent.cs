using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Results;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Consent.Api.Common.Observability;
using CareFlow.Consent.Api.Infrastructure.Persistence;
using CareFlow.Consent.Domain.Consents;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Consent.Api.Features.Consents;

internal sealed record GrantOutcome(ConsentRecord Consent, bool Created);

internal sealed partial class GrantConsentHandler(
    ConsentDbContext db,
    ITenantContext tenant,
    TimeProvider time,
    ConsentMetrics metrics,
    ILogger<GrantConsentHandler> logger)
{
    /// <summary>
    /// Concede (ou concede de novo) a finalidade. Repetir a mesma concessão ativa não muda nada e não publica
    /// evento, então o cliente pode repetir a chamada com segurança (PUT idempotente).
    /// </summary>
    public async Task<Result<GrantOutcome>> HandleAsync(
        Guid patientRef,
        ConsentPurpose purpose,
        LegalBasis legalBasis,
        string? termVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var consent = await db.Consents
            .FirstOrDefaultAsync(c => c.PatientRef == patientRef && c.Purpose == purpose, cancellationToken)
            .ConfigureAwait(false);

        var created = consent is null;
        ConsentChange change;
        if (consent is null)
        {
            var grant = ConsentRecord.Grant(tenant.TenantId, patientRef, purpose, legalBasis, termVersion, now);
            if (grant.IsFailure)
            {
                return grant.Error;
            }

            consent = grant.Value;
            db.Consents.Add(consent);
            change = ConsentChange.Granted;
        }
        else
        {
            var regrant = consent.Regrant(legalBasis, termVersion, now);
            if (regrant.IsFailure)
            {
                return regrant.Error;
            }

            change = regrant.Value;
        }

        if (change == ConsentChange.Unchanged)
        {
            return new GrantOutcome(consent, Created: false);
        }

        ConsentEvents.Granted(db, consent, actor, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConsentErrors.ConcurrentChange;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(ConsentConfiguration.PurposeIndex))
        {
            // Duas primeiras concessões simultâneas para a mesma finalidade: a outra venceu.
            return ConsentErrors.ConcurrentChange;
        }

        var purposeName = ConsentNames.Of(purpose);
        metrics.Granted(purposeName);
        LogGranted(logger, purposeName, consent.Version);
        return new GrantOutcome(consent, created);
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Consentimento concedido para {Purpose} (versão {Version})")]
    private static partial void LogGranted(ILogger logger, string purpose, long version);
}

internal static class GrantConsentEndpoint
{
    public static async Task<Results<Created<ConsentResponse>, Ok<ConsentResponse>, ProblemHttpResult>> HandleAsync(
        Guid patientRef,
        string purpose,
        GrantConsentRequest request,
        GrantConsentHandler handler,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!ConsentNames.TryParsePurpose(purpose, out var parsedPurpose))
        {
            return ConsentErrors.InvalidPurpose.ToProblem();
        }

        if (!ConsentNames.TryParseLegalBasis(request?.LegalBasis, out var legalBasis))
        {
            return ConsentErrors.InvalidLegalBasis.ToProblem();
        }

        var result = await handler.HandleAsync(
            patientRef, parsedPurpose, legalBasis, request!.TermVersion, AuditActor.From(context.User), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var (consent, created) = result.Value;
        var body = ConsentResponse.From(consent);
        return created
            ? TypedResults.Created($"/v1/patients/{patientRef}/consents/{purpose}", body)
            : TypedResults.Ok(body);
    }
}
