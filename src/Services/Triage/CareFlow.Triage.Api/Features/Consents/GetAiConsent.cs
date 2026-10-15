using CareFlow.Triage.Api.Infrastructure.Persistence;
using CareFlow.Triage.Domain.Consents;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Triage.Api.Features.Consents;

/// <summary>
/// Como o Triage vê o consentimento <c>ai-triage</c> do paciente agora: <c>granted</c>, <c>revoked</c> ou
/// <c>unknown</c> (nenhum evento chegou ainda). Responde "por que a IA não rodou?" sem consultar o Consent, que
/// continua sendo a fonte da verdade; a projeção chega por eventos e pode estar alguns instantes atrás.
/// </summary>
public sealed record AiConsentResponse(Guid PatientRef, string State, long Version, DateTimeOffset? UpdatedAt);

internal static class GetAiConsentEndpoint
{
    public static async Task<Ok<AiConsentResponse>> HandleAsync(
        Guid patientRef,
        TriageDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Paciente que a clínica nunca consentiu e paciente de outra clínica respondem igual (unknown): o filtro de
        // clínica e o RLS escondem a linha, e a resposta não revela se o pseudônimo existe em outro lugar.
        var consent = await db.Consents.AsNoTracking()
            .FirstOrDefaultAsync(c => c.PatientRef == patientRef, cancellationToken)
            .ConfigureAwait(false);

        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(consent is null
            ? new AiConsentResponse(patientRef, Slug(TriageConsentState.Unknown), 0, null)
            : new AiConsentResponse(patientRef, Slug(consent.State), consent.Version, consent.UpdatedAt));
    }

    private static string Slug(TriageConsentState state) => state.ToString().ToLowerInvariant();
}
