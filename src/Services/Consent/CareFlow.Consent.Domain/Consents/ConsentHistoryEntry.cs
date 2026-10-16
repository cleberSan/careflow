namespace CareFlow.Consent.Domain.Consents;

/// <summary>
/// Uma mudança de consentimento, como aconteceu: o registro em <see cref="ConsentRecord"/> guarda só a situação atual,
/// e a prova do consentimento (LGPD art. 8º, §2º) precisa de cada concessão e revogação com termo, base e data.
/// Só inserido, nunca alterado.
/// </summary>
public sealed class ConsentHistoryEntry
{
    private ConsentHistoryEntry()
    {
        // Usado pelo EF Core.
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConsentId { get; private set; }

    public Guid PatientRef { get; private set; }

    public ConsentPurpose Purpose { get; private set; }

    public LegalBasis LegalBasis { get; private set; }

    public string TermVersion { get; private set; } = string.Empty;

    public ConsentChange Change { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static ConsentHistoryEntry Of(ConsentRecord consent, ConsentChange change, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (change == ConsentChange.Unchanged)
        {
            throw new ArgumentException("Só mudanças entram no histórico.", nameof(change));
        }

        return new ConsentHistoryEntry
        {
            Id = Guid.CreateVersion7(now),
            TenantId = consent.TenantId,
            ConsentId = consent.Id,
            PatientRef = consent.PatientRef,
            Purpose = consent.Purpose,
            LegalBasis = consent.LegalBasis,
            TermVersion = consent.TermVersion,
            Change = change,
            Version = consent.Version,
            OccurredAt = change == ConsentChange.Revoked ? consent.RevokedAt!.Value : consent.GrantedAt,
        };
    }
}
