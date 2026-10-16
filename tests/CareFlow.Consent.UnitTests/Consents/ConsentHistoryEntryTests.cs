using CareFlow.Consent.Domain.Consents;

namespace CareFlow.Consent.UnitTests.Consents;

public sealed class ConsentHistoryEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static ConsentRecord Granted() =>
        ConsentRecord.Grant(Guid.NewGuid(), Guid.NewGuid(), ConsentPurpose.Notifications, LegalBasis.Consent, "2026-10", Now).Value;

    [Fact]
    public void Of_Grant_CopiesTermBasisVersionAndGrantDate()
    {
        // Arrange
        var consent = Granted();

        // Act
        var entry = ConsentHistoryEntry.Of(consent, ConsentChange.Granted, Now.AddMinutes(5));

        // Assert
        Assert.Equal(consent.Id, entry.ConsentId);
        Assert.Equal(consent.PatientRef, entry.PatientRef);
        Assert.Equal(ConsentChange.Granted, entry.Change);
        Assert.Equal("2026-10", entry.TermVersion);
        Assert.Equal(LegalBasis.Consent, entry.LegalBasis);
        Assert.Equal(1, entry.Version);
        Assert.Equal(consent.GrantedAt, entry.OccurredAt);
    }

    [Fact]
    public void Of_Revocation_UsesRevocationDateAndNewVersion()
    {
        // Arrange
        var consent = Granted();
        consent.Revoke(Now.AddDays(1));

        // Act
        var entry = ConsentHistoryEntry.Of(consent, ConsentChange.Revoked, Now.AddDays(1));

        // Assert
        Assert.Equal(ConsentChange.Revoked, entry.Change);
        Assert.Equal(2, entry.Version);
        Assert.Equal(consent.RevokedAt, entry.OccurredAt);
    }

    [Fact]
    public void Of_Unchanged_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => ConsentHistoryEntry.Of(Granted(), ConsentChange.Unchanged, Now));
    }
}
