using CareFlow.Consent.Domain.Consents;

namespace CareFlow.Consent.Api.Features.Consents;

/// <summary>Concessão para uma finalidade. <c>legalBasis</c>: consent, specific-consent ou health-protection.</summary>
public sealed record GrantConsentRequest(string? LegalBasis, string? TermVersion);

public sealed record ConsentResponse(
    string Purpose,
    string LegalBasis,
    string TermVersion,
    string Status,
    DateTimeOffset GrantedAt,
    DateTimeOffset? RevokedAt,
    long Version)
{
    internal static ConsentResponse From(ConsentRecord consent) =>
        new(
            ConsentNames.Of(consent.Purpose),
            ConsentNames.Of(consent.LegalBasis),
            consent.TermVersion,
            consent.Status == ConsentStatus.Granted ? "granted" : "revoked",
            consent.GrantedAt,
            consent.RevokedAt,
            consent.Version);
}
