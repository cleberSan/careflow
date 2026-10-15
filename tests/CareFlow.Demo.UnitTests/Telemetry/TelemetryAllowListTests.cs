using CareFlow.Demo.Api.Telemetry;

namespace CareFlow.Demo.UnitTests.Telemetry;

public sealed class TelemetryAllowListTests
{
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef0", false)]
    [InlineData("../../api/admin/0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcde\n", false)]
    [InlineData(null, false)]
    public void IsValidTraceId_AcceptsOnly32LowercaseHexDigits(string? traceId, bool expected) =>
        Assert.Equal(expected, TelemetryAllowList.IsValidTraceId(traceId));

    [Fact]
    public void Filter_KeepsAllowedKeys_DropsEverythingElse_AndTruncates()
    {
        // Arrange
        var attributes = new Dictionary<string, string>
        {
            ["http.route"] = "/v1/appointments",
            ["url.full"] = "https://vault/v1/patients?cpf=12345678909",
            ["db.query.text"] = "SELECT * FROM patients",
            ["careflow.channel"] = new string('x', 300),
            ["careflow." + new string('k', 80)] = "longa demais",
            ["enduser.id"] = "alguém",
        };

        // Act
        var filtered = TelemetryAllowList.Filter(attributes);

        // Assert
        Assert.Equal(["careflow.channel", "http.route"], filtered.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(128, filtered["careflow.channel"].Length);
    }

    [Theory]
    [InlineData("fallback", true)]
    [InlineData("circuit open", true)]
    [InlineData("exception", false)]
    [InlineData("Fallback", false)]
    public void AllowsEvent_IsAnExactAllowList(string name, bool expected) =>
        Assert.Equal(expected, TelemetryAllowList.AllowsEvent(name));

    [Fact]
    public void FilterEvent_KeepsOnlyChannelTransitionAttributes()
    {
        var filtered = TelemetryAllowList.FilterEvent(new Dictionary<string, string>
        {
            ["from"] = "whatsapp",
            ["to"] = "sms",
            ["exception.message"] = "telefone 5511999999999 recusado",
        });

        Assert.Equal(["from", "to"], filtered.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task UnavailableReader_ReturnsNothing()
    {
        var reader = new UnavailableTelemetryReader();

        Assert.Null(await reader.TraceAsync(new string('a', 32), TestContext.Current.CancellationToken));
        Assert.Empty(await reader.LogsAsync(new string('a', 32), TestContext.Current.CancellationToken));
    }
}
