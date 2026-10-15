namespace CareFlow.Consent.Domain.Consents;

/// <summary>Base legal do tratamento. Só as bases de consentimento podem ser revogadas pelo titular.</summary>
public enum LegalBasis
{
    /// <summary>Consentimento do titular (LGPD art. 7º, I).</summary>
    Consent = 1,

    /// <summary>Consentimento específico e destacado para dado sensível (LGPD art. 11, I).</summary>
    SpecificConsent,

    /// <summary>Tutela da saúde por profissional ou serviço de saúde (LGPD art. 11, II, f). Não depende de consentimento.</summary>
    HealthProtection,
}

public static class LegalBasisExtensions
{
    /// <summary>LGPD art. 8º, § 5º: o consentimento pode ser revogado a qualquer momento. As outras bases, não.</summary>
    public static bool IsRevocable(this LegalBasis basis) =>
        basis is LegalBasis.Consent or LegalBasis.SpecificConsent;
}
