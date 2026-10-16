using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Consent.Domain.Consents;

/// <summary>
/// Situação atual do consentimento de um paciente (pelo pseudônimo <see cref="PatientRef"/>) para uma
/// finalidade. <see cref="Version"/> cresce a cada mudança e vai nos eventos: quem consome aplica só
/// versões maiores que a que já conhece, então eventos repetidos ou fora de ordem não desfazem uma revogação.
/// </summary>
public sealed class ConsentRecord
{
    public const int MaxTermVersionLength = 32;

    private ConsentRecord()
    {
        // Usado pelo EF Core.
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PatientRef { get; private set; }

    public ConsentPurpose Purpose { get; private set; }

    public LegalBasis LegalBasis { get; private set; }

    public string TermVersion { get; private set; } = string.Empty;

    public ConsentStatus Status { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public long Version { get; private set; }

    public static Result<ConsentRecord> Grant(
        Guid tenantId,
        Guid patientRef,
        ConsentPurpose purpose,
        LegalBasis legalBasis,
        string? termVersion,
        DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || patientRef == Guid.Empty)
        {
            return ConsentErrors.MissingIdentifier;
        }

        var validation = Validate(purpose, legalBasis, termVersion);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        return new ConsentRecord
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            PatientRef = patientRef,
            Purpose = purpose,
            LegalBasis = legalBasis,
            TermVersion = termVersion!,
            Status = ConsentStatus.Granted,
            GrantedAt = ToStoragePrecision(now),
            Version = 1,
        };
    }

    /// <summary>
    /// Concede de novo (após revogação, ou com outro termo ou base legal). Repetir a mesma concessão
    /// ativa não muda nada.
    /// </summary>
    public Result<ConsentChange> Regrant(LegalBasis legalBasis, string? termVersion, DateTimeOffset now)
    {
        var validation = Validate(Purpose, legalBasis, termVersion);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        if (Status == ConsentStatus.Granted
            && LegalBasis == legalBasis
            && string.Equals(TermVersion, termVersion, StringComparison.Ordinal))
        {
            return ConsentChange.Unchanged;
        }

        LegalBasis = legalBasis;
        TermVersion = termVersion!;
        Status = ConsentStatus.Granted;
        GrantedAt = ToStoragePrecision(now);
        RevokedAt = null;
        Version++;
        return ConsentChange.Granted;
    }

    /// <summary>Revoga. Revogar de novo não muda nada; bases que não são consentimento não podem ser revogadas.</summary>
    public Result<ConsentChange> Revoke(DateTimeOffset now)
    {
        if (!LegalBasis.IsRevocable())
        {
            return ConsentErrors.NotRevocable;
        }

        if (Status == ConsentStatus.Revoked)
        {
            return ConsentChange.Unchanged;
        }

        Status = ConsentStatus.Revoked;
        RevokedAt = ToStoragePrecision(now);
        Version++;
        return ConsentChange.Revoked;
    }

    private static Result Validate(ConsentPurpose purpose, LegalBasis legalBasis, string? termVersion)
    {
        if (!Enum.IsDefined(purpose))
        {
            return Result.Failure(ConsentErrors.InvalidPurpose);
        }

        if (!Enum.IsDefined(legalBasis))
        {
            return Result.Failure(ConsentErrors.InvalidLegalBasis);
        }

        // A triagem por IA é opcional para o cuidado: não cabe em tutela da saúde e trata dado sensível, então só
        // vale com consentimento específico e destacado (LGPD art. 11, I), sempre revogável.
        if (purpose == ConsentPurpose.AiTriage && legalBasis != LegalBasis.SpecificConsent)
        {
            return Result.Failure(ConsentErrors.LegalBasisNotAllowedForPurpose);
        }

        return IsTermVersion(termVersion) ? Result.Success() : Result.Failure(ConsentErrors.InvalidTermVersion);
    }

    private static bool IsTermVersion(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTermVersionLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
