using CareFlow.Scheduling.Domain.Appointments;

namespace CareFlow.Scheduling.UnitTests.Appointments;

public sealed class SchedulingPolicyTests
{
    [Fact]
    public void Constructor_WithValidLimits_KeepsValues()
    {
        // Act
        var policy = new SchedulingPolicy(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60));

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(15), policy.MinimumDuration);
        Assert.Equal(TimeSpan.FromMinutes(60), policy.MaximumDuration);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-5, 60)]
    [InlineData(30, 20)]
    public void Constructor_WithInvalidLimits_Throws(int minimumMinutes, int maximumMinutes)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SchedulingPolicy(TimeSpan.FromMinutes(minimumMinutes), TimeSpan.FromMinutes(maximumMinutes)));
    }

    [Fact]
    public void DurationOutOfRange_DescribesLimitsInMinutes()
    {
        // Arrange
        var policy = new SchedulingPolicy(TimeSpan.FromMinutes(10), TimeSpan.FromHours(4));

        // Act
        var error = AppointmentErrors.DurationOutOfRange(policy);

        // Assert
        Assert.Contains("10 e 240 minutos", error.Message, StringComparison.Ordinal);
    }
}
