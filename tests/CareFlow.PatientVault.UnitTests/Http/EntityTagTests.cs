using CareFlow.PatientVault.Api.Common.Http;
using CareFlow.PatientVault.Domain.Patients;

namespace CareFlow.PatientVault.UnitTests.Http;

public sealed class EntityTagTests
{
    [Fact]
    public void FormatThenParse_RoundTrips()
    {
        // Act
        var parsed = EntityTag.ParseIfMatch(EntityTag.Format(4_000_000_000));

        // Assert
        Assert.Equal(4_000_000_000u, parsed.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseIfMatch_Missing_RequiresPrecondition(string? value)
    {
        // Assert
        Assert.Equal(PatientErrors.VersionRequired, EntityTag.ParseIfMatch(value).Error);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("12")]
    [InlineData("W/\"12\"")]
    [InlineData("\"12\", \"13\"")]
    [InlineData("\"\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"abc\"")]
    [InlineData("\"99999999999\"")]
    public void ParseIfMatch_AnythingButOneStrongTag_FailsAsStale(string value)
    {
        // Assert
        Assert.Equal(PatientErrors.StaleVersion, EntityTag.ParseIfMatch(value).Error);
    }
}
