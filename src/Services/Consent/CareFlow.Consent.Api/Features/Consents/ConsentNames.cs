using CareFlow.Consent.Domain.Consents;
using CareFlow.Contracts.Consents;

namespace CareFlow.Consent.Api.Features.Consents;

/// <summary>Nomes estáveis de finalidade e base legal, usados na URL, nas respostas e nos eventos.</summary>
internal static class ConsentNames
{
    private static readonly Dictionary<ConsentPurpose, string> Purposes = new()
    {
        [ConsentPurpose.Scheduling] = ConsentPurposes.Scheduling,
        [ConsentPurpose.Notifications] = ConsentPurposes.Notifications,
        [ConsentPurpose.AiTriage] = ConsentPurposes.AiTriage,
    };

    private static readonly Dictionary<LegalBasis, string> LegalBases = new()
    {
        [LegalBasis.Consent] = "consent",
        [LegalBasis.SpecificConsent] = "specific-consent",
        [LegalBasis.HealthProtection] = "health-protection",
    };

    public static string Of(ConsentPurpose purpose) => Purposes[purpose];

    public static string Of(LegalBasis legalBasis) => LegalBases[legalBasis];

    public static bool TryParsePurpose(string? value, out ConsentPurpose purpose) => TryParse(Purposes, value, out purpose);

    public static bool TryParseLegalBasis(string? value, out LegalBasis legalBasis) => TryParse(LegalBases, value, out legalBasis);

    private static bool TryParse<T>(Dictionary<T, string> names, string? value, out T result)
        where T : struct, Enum
    {
        foreach (var (key, name) in names)
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                result = key;
                return true;
            }
        }

        result = default;
        return false;
    }
}
