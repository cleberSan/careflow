using CareFlow.Consent.Domain.Consents;

namespace CareFlow.Consent.UnitTests.Consents;

public sealed class ConsentRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static ConsentRecord Granted(LegalBasis basis = LegalBasis.SpecificConsent, string term = "2026-10") =>
        ConsentRecord.Grant(Guid.NewGuid(), Guid.NewGuid(), ConsentPurpose.Scheduling, basis, term, Now).Value;

    [Fact]
    public void Grant_WithValidData_StartsGrantedAtVersionOne()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var patientRef = Guid.NewGuid();

        // Act
        var result = ConsentRecord.Grant(tenantId, patientRef, ConsentPurpose.AiTriage, LegalBasis.SpecificConsent, "v1.2", Now.AddTicks(3));

        // Assert
        Assert.True(result.IsSuccess);
        var consent = result.Value;
        Assert.NotEqual(Guid.Empty, consent.Id);
        Assert.Equal(tenantId, consent.TenantId);
        Assert.Equal(patientRef, consent.PatientRef);
        Assert.Equal(ConsentPurpose.AiTriage, consent.Purpose);
        Assert.Equal(LegalBasis.SpecificConsent, consent.LegalBasis);
        Assert.Equal("v1.2", consent.TermVersion);
        Assert.Equal(ConsentStatus.Granted, consent.Status);
        Assert.Equal(Now, consent.GrantedAt);
        Assert.Null(consent.RevokedAt);
        Assert.Equal(1, consent.Version);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Grant_WithEmptyIdentifier_Fails(bool emptyTenant, bool emptyPatient)
    {
        // Act
        var result = ConsentRecord.Grant(
            emptyTenant ? Guid.Empty : Guid.NewGuid(),
            emptyPatient ? Guid.Empty : Guid.NewGuid(),
            ConsentPurpose.Scheduling,
            LegalBasis.Consent,
            "v1",
            Now);

        // Assert
        Assert.Equal(ConsentErrors.MissingIdentifier, result.Error);
    }

    [Fact]
    public void Grant_WithUnknownPurpose_Fails()
    {
        // Act
        var result = ConsentRecord.Grant(Guid.NewGuid(), Guid.NewGuid(), (ConsentPurpose)99, LegalBasis.Consent, "v1", Now);

        // Assert
        Assert.Equal(ConsentErrors.InvalidPurpose, result.Error);
    }

    [Fact]
    public void Grant_WithUnknownLegalBasis_Fails()
    {
        // Act
        var result = ConsentRecord.Grant(Guid.NewGuid(), Guid.NewGuid(), ConsentPurpose.Scheduling, (LegalBasis)0, "v1", Now);

        // Assert
        Assert.Equal(ConsentErrors.InvalidLegalBasis, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("versão")]
    [InlineData("v 1")]
    [InlineData("123456789012345678901234567890123")]
    public void Grant_WithInvalidTermVersion_Fails(string? term)
    {
        // Act
        var result = ConsentRecord.Grant(Guid.NewGuid(), Guid.NewGuid(), ConsentPurpose.Scheduling, LegalBasis.Consent, term, Now);

        // Assert
        Assert.Equal(ConsentErrors.InvalidTermVersion, result.Error);
    }

    [Fact]
    public void Grant_AcceptsTermVersionAtMaximumLength()
    {
        // Act
        var result = ConsentRecord.Grant(
            Guid.NewGuid(), Guid.NewGuid(), ConsentPurpose.Scheduling, LegalBasis.Consent, new string('a', ConsentRecord.MaxTermVersionLength), Now);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Revoke_Granted_RevokesAndIncrementsVersion()
    {
        // Arrange
        var consent = Granted();

        // Act
        var result = consent.Revoke(Now.AddDays(1));

        // Assert
        Assert.Equal(ConsentChange.Revoked, result.Value);
        Assert.Equal(ConsentStatus.Revoked, consent.Status);
        Assert.Equal(Now.AddDays(1), consent.RevokedAt);
        Assert.Equal(2, consent.Version);
    }

    [Fact]
    public void Revoke_Twice_IsIdempotent()
    {
        // Arrange
        var consent = Granted();
        consent.Revoke(Now);

        // Act
        var result = consent.Revoke(Now.AddHours(1));

        // Assert
        Assert.Equal(ConsentChange.Unchanged, result.Value);
        Assert.Equal(2, consent.Version);
        Assert.Equal(Now, consent.RevokedAt);
    }

    [Fact]
    public void Revoke_NonConsentLegalBasis_IsRefused()
    {
        // Arrange
        var consent = Granted(LegalBasis.HealthProtection);

        // Act
        var result = consent.Revoke(Now);

        // Assert
        Assert.Equal(ConsentErrors.NotRevocable, result.Error);
        Assert.Equal(ConsentStatus.Granted, consent.Status);
        Assert.Equal(1, consent.Version);
    }

    [Fact]
    public void Regrant_SameActiveGrant_IsIdempotent()
    {
        // Arrange
        var consent = Granted();

        // Act
        var result = consent.Regrant(LegalBasis.SpecificConsent, "2026-10", Now.AddDays(1));

        // Assert
        Assert.Equal(ConsentChange.Unchanged, result.Value);
        Assert.Equal(1, consent.Version);
        Assert.Equal(Now, consent.GrantedAt);
    }

    [Fact]
    public void Regrant_AfterRevocation_GrantsAgainWithNewVersion()
    {
        // Arrange
        var consent = Granted();
        consent.Revoke(Now);

        // Act
        var result = consent.Regrant(LegalBasis.SpecificConsent, "2026-10", Now.AddDays(2));

        // Assert
        Assert.Equal(ConsentChange.Granted, result.Value);
        Assert.Equal(ConsentStatus.Granted, consent.Status);
        Assert.Null(consent.RevokedAt);
        Assert.Equal(Now.AddDays(2), consent.GrantedAt);
        Assert.Equal(3, consent.Version);
    }

    [Fact]
    public void Regrant_WithNewTermVersion_UpdatesTerm()
    {
        // Arrange
        var consent = Granted(term: "2026-10");

        // Act
        var result = consent.Regrant(LegalBasis.SpecificConsent, "2027-01", Now);

        // Assert
        Assert.Equal(ConsentChange.Granted, result.Value);
        Assert.Equal("2027-01", consent.TermVersion);
        Assert.Equal(2, consent.Version);
    }

    [Fact]
    public void Regrant_WithNewLegalBasis_UpdatesBasis()
    {
        // Arrange
        var consent = Granted(LegalBasis.Consent);

        // Act
        var result = consent.Regrant(LegalBasis.HealthProtection, "2026-10", Now);

        // Assert
        Assert.Equal(ConsentChange.Granted, result.Value);
        Assert.Equal(LegalBasis.HealthProtection, consent.LegalBasis);
    }

    [Fact]
    public void Regrant_WithInvalidTerm_FailsWithoutChanges()
    {
        // Arrange
        var consent = Granted();

        // Act
        var result = consent.Regrant(LegalBasis.Consent, "inválida", Now);

        // Assert
        Assert.Equal(ConsentErrors.InvalidTermVersion, result.Error);
        Assert.Equal(1, consent.Version);
    }

    [Theory]
    [InlineData(LegalBasis.Consent, true)]
    [InlineData(LegalBasis.SpecificConsent, true)]
    [InlineData(LegalBasis.HealthProtection, false)]
    public void IsRevocable_OnlyForConsentBases(LegalBasis basis, bool expected)
    {
        // Act & Assert
        Assert.Equal(expected, basis.IsRevocable());
    }
}
