using CareFlow.BuildingBlocks.Results;
using CareFlow.Scheduling.Domain.Appointments;

namespace CareFlow.Scheduling.UnitTests.Appointments;

public sealed class AppointmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly SchedulingPolicy Policy = new(TimeSpan.FromMinutes(10), TimeSpan.FromHours(4));
    private static readonly DateTimeOffset Tomorrow9h = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static BookingRequest Request(
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        Guid? tenantId = null,
        Guid? professionalId = null,
        Guid? patientRef = null) =>
        new(
            tenantId ?? Guid.NewGuid(),
            professionalId ?? Guid.NewGuid(),
            patientRef ?? Guid.NewGuid(),
            startsAt ?? Tomorrow9h,
            endsAt ?? Tomorrow9h.AddMinutes(30),
            "chave-1",
            "fingerprint-1");

    [Fact]
    public void Book_WithValidRequest_CreatesBookedAppointment()
    {
        // Arrange
        var request = Request();

        // Act
        var result = Appointment.Book(request, Policy, Now);

        // Assert
        Assert.True(result.IsSuccess);
        var appointment = result.Value;
        Assert.NotEqual(Guid.Empty, appointment.Id);
        Assert.Equal(request.TenantId, appointment.TenantId);
        Assert.Equal(request.ProfessionalId, appointment.ProfessionalId);
        Assert.Equal(request.PatientRef, appointment.PatientRef);
        Assert.Equal(Tomorrow9h, appointment.StartsAt);
        Assert.Equal(AppointmentStatus.Booked, appointment.Status);
        Assert.Equal(Now, appointment.CreatedAt);
        Assert.Null(appointment.CancelledAt);
        Assert.Equal("chave-1", appointment.IdempotencyKey);
        Assert.Equal("fingerprint-1", appointment.RequestFingerprint);
    }

    [Fact]
    public void Book_NormalizesTimesToUtc()
    {
        // Arrange: 06:00 em São Paulo (UTC-3) é 09:00 UTC.
        var saoPaulo = TimeSpan.FromHours(-3);
        var request = Request(
            startsAt: new DateTimeOffset(2026, 10, 15, 6, 0, 0, saoPaulo),
            endsAt: new DateTimeOffset(2026, 10, 15, 6, 30, 0, saoPaulo));

        // Act
        var appointment = Appointment.Book(request, Policy, Now).Value;

        // Assert
        Assert.Equal(TimeSpan.Zero, appointment.StartsAt.Offset);
        Assert.Equal(Tomorrow9h, appointment.StartsAt);
    }

    [Fact]
    public void Book_TruncatesTimestampsToMicroseconds()
    {
        // Arrange: 1 tick = 100 ns, abaixo da precisão do banco.
        var now = Now.AddTicks(12_345_678);

        // Act
        var appointment = Appointment.Book(Request(), Policy, now).Value;
        appointment.Cancel(now);

        // Assert
        Assert.Equal(Now.AddTicks(12_345_670), appointment.CreatedAt);
        Assert.Equal(Now.AddTicks(12_345_670), appointment.CancelledAt);
    }

    public static TheoryData<Guid, Guid, Guid> MissingIdentifiers => new()
    {
        { Guid.Empty, Guid.NewGuid(), Guid.NewGuid() },
        { Guid.NewGuid(), Guid.Empty, Guid.NewGuid() },
        { Guid.NewGuid(), Guid.NewGuid(), Guid.Empty },
    };

    [Theory]
    [MemberData(nameof(MissingIdentifiers))]
    public void Book_WithEmptyIdentifier_FailsWithValidation(Guid tenantId, Guid professionalId, Guid patientRef)
    {
        // Act
        var result = Appointment.Book(Request(tenantId: tenantId, professionalId: professionalId, patientRef: patientRef), Policy, Now);

        // Assert
        Assert.Equal(AppointmentErrors.MissingIdentifier, result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Book_WithEndNotAfterStart_FailsWithInvalidInterval(int durationMinutes)
    {
        // Act
        var result = Appointment.Book(Request(endsAt: Tomorrow9h.AddMinutes(durationMinutes)), Policy, Now);

        // Assert
        Assert.Equal(AppointmentErrors.InvalidInterval, result.Error);
    }

    [Theory]
    [InlineData(30, 0)]
    [InlineData(0, 30)]
    public void Book_WithSecondsInTimes_FailsWithNotAligned(int startSeconds, int endSeconds)
    {
        // Arrange
        var request = Request(
            startsAt: Tomorrow9h.AddSeconds(startSeconds),
            endsAt: Tomorrow9h.AddMinutes(30).AddSeconds(endSeconds));

        // Act
        var result = Appointment.Book(request, Policy, Now);

        // Assert
        Assert.Equal(AppointmentErrors.NotAlignedToMinute, result.Error);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(240, true)]
    [InlineData(241, false)]
    public void Book_DurationBoundaries(int durationMinutes, bool expectedSuccess)
    {
        // Act
        var result = Appointment.Book(Request(endsAt: Tomorrow9h.AddMinutes(durationMinutes)), Policy, Now);

        // Assert
        Assert.Equal(expectedSuccess, result.IsSuccess);
        if (!expectedSuccess)
        {
            Assert.Equal("Agenda.DuracaoForaDoLimite", result.Error.Code);
            Assert.Equal(ErrorType.Validation, result.Error.Type);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Book_StartingNowOrInThePast_Fails(int minutesFromNow)
    {
        // Arrange
        var startsAt = Now.AddMinutes(minutesFromNow);

        // Act
        var result = Appointment.Book(Request(startsAt: startsAt, endsAt: startsAt.AddMinutes(30)), Policy, Now);

        // Assert
        Assert.Equal(AppointmentErrors.StartsInThePast, result.Error);
    }

    [Fact]
    public void Book_StartingOneMinuteFromNow_Succeeds()
    {
        // Arrange
        var startsAt = Now.AddMinutes(1);

        // Act
        var result = Appointment.Book(Request(startsAt: startsAt, endsAt: startsAt.AddMinutes(30)), Policy, Now);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("", "fp")]
    [InlineData("chave", " ")]
    public void Book_WithoutIdempotencyData_Throws(string key, string fingerprint)
    {
        // Arrange
        var request = Request() with { IdempotencyKey = key, Fingerprint = fingerprint };

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => Appointment.Book(request, Policy, Now));
    }

    [Fact]
    public void Cancel_BookedFutureAppointment_Succeeds()
    {
        // Arrange
        var appointment = Appointment.Book(Request(), Policy, Now).Value;
        var cancelledAt = Now.AddHours(1);

        // Act
        var result = appointment.Cancel(cancelledAt);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.Equal(cancelledAt, appointment.CancelledAt);
    }

    [Fact]
    public void Cancel_Twice_FailsWithAlreadyCancelled()
    {
        // Arrange
        var appointment = Appointment.Book(Request(), Policy, Now).Value;
        appointment.Cancel(Now);

        // Act
        var result = appointment.Cancel(Now);

        // Assert
        Assert.Equal(AppointmentErrors.AlreadyCancelled, result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Cancel_AtOrAfterStart_FailsWithAlreadyStarted(int minutesAfterStart)
    {
        // Arrange
        var appointment = Appointment.Book(Request(), Policy, Now).Value;

        // Act
        var result = appointment.Cancel(Tomorrow9h.AddMinutes(minutesAfterStart));

        // Assert
        Assert.Equal(AppointmentErrors.AlreadyStarted, result.Error);
        Assert.Equal(AppointmentStatus.Booked, appointment.Status);
    }

    [Fact]
    public void Cancel_ByRequest_RecordsRequestedReason()
    {
        // Arrange
        var appointment = Appointment.Book(Request(), Policy, Now).Value;

        // Act
        appointment.Cancel(Now);

        // Assert
        Assert.Equal(CancellationReason.Requested, appointment.CancellationReason);
    }

    [Fact]
    public void Book_NewAppointment_HasNoCancellationReason()
    {
        // Act
        var appointment = Appointment.Book(Request(), Policy, Now).Value;

        // Assert
        Assert.Null(appointment.CancellationReason);
    }

    [Fact]
    public void CancelForConsentRevocation_FutureAppointment_RecordsConsentRevokedReason()
    {
        // Arrange
        var appointment = Appointment.Book(Request(), Policy, Now).Value;

        // Act
        var result = appointment.CancelForConsentRevocation(Now);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.Equal(CancellationReason.ConsentRevoked, appointment.CancellationReason);
    }

    [Fact]
    public void CancelForConsentRevocation_StartedAppointment_KeepsItBooked()
    {
        // Arrange
        var appointment = Appointment.Book(Request(), Policy, Now).Value;

        // Act
        var result = appointment.CancelForConsentRevocation(Tomorrow9h.AddMinutes(5));

        // Assert
        Assert.Equal(AppointmentErrors.AlreadyStarted, result.Error);
        Assert.Equal(AppointmentStatus.Booked, appointment.Status);
        Assert.Null(appointment.CancellationReason);
    }

    [Fact]
    public void CancelForSubjectErasure_FutureAppointment_RecordsSubjectErasureReason()
    {
        // Arrange
        var appointment = Appointment.Book(Request(), Policy, Now).Value;

        // Act
        var result = appointment.CancelForSubjectErasure(Now);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.Equal(CancellationReason.SubjectErasure, appointment.CancellationReason);
    }

    [Fact]
    public void CancelForSubjectErasure_StartedAppointment_IsKept()
    {
        // Arrange
        var appointment = Appointment.Book(Request(), Policy, Now).Value;

        // Act
        var result = appointment.CancelForSubjectErasure(Tomorrow9h.AddMinutes(5));

        // Assert
        Assert.Equal(AppointmentErrors.AlreadyStarted, result.Error);
        Assert.Equal(AppointmentStatus.Booked, appointment.Status);
    }
}
