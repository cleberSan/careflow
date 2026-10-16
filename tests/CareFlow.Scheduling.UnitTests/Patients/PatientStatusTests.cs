using CareFlow.Scheduling.Domain.Patients;

namespace CareFlow.Scheduling.UnitTests.Patients;

public sealed class PatientStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static PatientStatus NewStatus() => PatientStatus.Track(Guid.NewGuid(), Guid.NewGuid(), Now);

    [Fact]
    public void Track_StartsUnknownAndUnregistered()
    {
        // Act
        var status = NewStatus();

        // Assert
        Assert.False(status.IsRegistered);
        Assert.Equal(SchedulingConsent.Unknown, status.SchedulingConsent);
        Assert.Equal(0, status.ConsentVersion);
        Assert.Equal(Now, status.UpdatedAt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Track_WithEmptyIds_Throws(bool emptyTenant, bool emptyPatient)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => PatientStatus.Track(
            emptyTenant ? Guid.Empty : Guid.NewGuid(), emptyPatient ? Guid.Empty : Guid.NewGuid(), Now));
    }

    [Fact]
    public void EnsureCanBook_Unregistered_FailsWithNotYetKnown()
    {
        // Arrange
        var status = NewStatus();
        status.ApplyConsentGranted(1, Now);

        // Act
        var result = status.EnsureCanBook();

        // Assert
        Assert.Equal(PatientErrors.NotYetKnown, result.Error);
    }

    [Fact]
    public void EnsureCanBook_RegisteredWithoutConsent_FailsWithNoLegalBasis()
    {
        // Arrange
        var status = NewStatus();
        status.MarkRegistered(Now);

        // Act
        var result = status.EnsureCanBook();

        // Assert
        Assert.Equal(PatientErrors.NoLegalBasis, result.Error);
    }

    [Fact]
    public void EnsureCanBook_RegisteredAndGranted_Succeeds()
    {
        // Arrange
        var status = NewStatus();
        status.MarkRegistered(Now);
        status.ApplyConsentGranted(1, Now);

        // Act
        var result = status.EnsureCanBook();

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void EnsureCanBook_AfterRevocation_FailsWithNoLegalBasis()
    {
        // Arrange
        var status = NewStatus();
        status.MarkRegistered(Now);
        status.ApplyConsentGranted(1, Now);
        status.ApplyConsentRevoked(2, Now);

        // Act
        var result = status.EnsureCanBook();

        // Assert
        Assert.Equal(PatientErrors.NoLegalBasis, result.Error);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ApplyConsentGranted_StaleOrDuplicateVersion_IsIgnored(long staleVersion)
    {
        // Arrange
        var status = NewStatus();
        status.ApplyConsentRevoked(3, Now);
        var later = Now.AddMinutes(1);

        // Act
        var applied = status.ApplyConsentGranted(staleVersion, later);

        // Assert
        Assert.False(applied);
        Assert.Equal(SchedulingConsent.Revoked, status.SchedulingConsent);
        Assert.Equal(3, status.ConsentVersion);
        Assert.Equal(Now, status.UpdatedAt);
    }

    [Fact]
    public void ApplyConsentRevoked_StaleVersion_IsIgnored()
    {
        // Arrange
        var status = NewStatus();
        status.ApplyConsentGranted(5, Now);

        // Act
        var applied = status.ApplyConsentRevoked(4, Now);

        // Assert
        Assert.False(applied);
        Assert.Equal(SchedulingConsent.Granted, status.SchedulingConsent);
    }

    [Fact]
    public void ApplyConsent_NewerVersion_IsApplied()
    {
        // Arrange
        var status = NewStatus();
        status.ApplyConsentGranted(1, Now);
        var later = Now.AddMinutes(1);

        // Act
        var applied = status.ApplyConsentRevoked(2, later);

        // Assert
        Assert.True(applied);
        Assert.Equal(SchedulingConsent.Revoked, status.SchedulingConsent);
        Assert.Equal(2, status.ConsentVersion);
        Assert.Equal(later, status.UpdatedAt);
    }

    [Fact]
    public void MarkRegistered_Twice_KeepsFirstTimestamp()
    {
        // Arrange
        var status = NewStatus();
        var first = Now.AddMinutes(1);
        status.MarkRegistered(first);

        // Act
        status.MarkRegistered(Now.AddMinutes(2));

        // Assert
        Assert.True(status.IsRegistered);
        Assert.Equal(first, status.UpdatedAt);
    }

    [Fact]
    public void MarkErased_BlocksBooking_EvenWithConsentGrantedLater()
    {
        // Arrange
        var status = NewStatus();
        status.MarkRegistered(Now);
        status.ApplyConsentGranted(1, Now);

        // Act
        var first = status.MarkErased(Now.AddMinutes(1));
        status.ApplyConsentGranted(2, Now.AddMinutes(2));

        // Assert
        Assert.True(first);
        Assert.True(status.IsErased);
        Assert.Equal(PatientErrors.Erased, status.EnsureCanBook().Error);
    }

    [Fact]
    public void MarkErased_Twice_KeepsTheOriginalDate()
    {
        // Arrange
        var status = NewStatus();
        status.MarkErased(Now);

        // Act
        var second = status.MarkErased(Now.AddDays(1));

        // Assert
        Assert.False(second);
        Assert.Equal(Now, status.ErasedAt);
    }
}
