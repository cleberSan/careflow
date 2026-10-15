using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Clinics;

using static CareFlow.Notifications.UnitTests.Notifications.Fixtures;

namespace CareFlow.Notifications.UnitTests.Notifications;

public sealed class ClinicSettingsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Theory]
    [InlineData("Clínica São José")]
    [InlineData("Dr. Silva & Filhos")]
    [InlineData("  Clinica   Centro  ")]
    [InlineData("AB")]
    public void Create_AcceptsPlainNames(string name)
    {
        var created = ClinicSettings.Create(Tenant, name, "America/Sao_Paulo", ChannelOrder.Default, TimeSpan.FromHours(24), Now);

        Assert.True(created.IsSuccess);
        Assert.DoesNotContain("  ", created.Value.DisplayName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("A")]
    [InlineData("www.golpe.com")]
    [InlineData("Clinica golpe.com")]
    [InlineData("https://x")]
    [InlineData("Clinica @ex")]
    [InlineData("Clinica\nNova")]
    [InlineData("Clinica <b>")]
    public void Create_RejectsNamesThatCouldCarryLinksOrMarkup(string? name)
    {
        var created = ClinicSettings.Create(Tenant, name, "America/Sao_Paulo", ChannelOrder.Default, TimeSpan.FromHours(24), Now);

        Assert.Equal(ClinicErrors.InvalidDisplayName, created.Error);
    }

    [Fact]
    public void Create_RejectsLongName()
    {
        var created = ClinicSettings.Create(Tenant, new string('a', ClinicSettings.MaxDisplayNameLength + 1), "America/Sao_Paulo", ChannelOrder.Default, TimeSpan.FromHours(24), Now);
        Assert.Equal(ClinicErrors.InvalidDisplayName, created.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Marte/Olympus")]
    public void Create_RejectsUnknownTimeZone(string? zone)
    {
        var created = ClinicSettings.Create(Tenant, "Clinica", zone, ChannelOrder.Default, TimeSpan.FromHours(24), Now);
        Assert.Equal(ClinicErrors.InvalidTimeZone, created.Error);
    }

    [Theory]
    [InlineData(59)]
    [InlineData(72 * 60 + 1)]
    public void Create_RejectsLeadOutOfRange(int minutes)
    {
        var created = ClinicSettings.Create(Tenant, "Clinica", "America/Sao_Paulo", ChannelOrder.Default, TimeSpan.FromMinutes(minutes), Now);
        Assert.Equal(ClinicErrors.InvalidReminderLead, created.Error);
    }

    [Fact]
    public void Create_RejectsLeadWithSeconds()
    {
        var created = ClinicSettings.Create(Tenant, "Clinica", "America/Sao_Paulo", ChannelOrder.Default, TimeSpan.FromMinutes(90).Add(TimeSpan.FromSeconds(1)), Now);
        Assert.Equal(ClinicErrors.InvalidReminderLead, created.Error);
    }

    [Fact]
    public void Update_ChangesEverythingAtOnce()
    {
        var settings = Clinic();
        var order = ChannelOrder.Create([Channel.Email, Channel.Sms]).Value;

        var updated = settings.Update("Outra Clinica", "America/Manaus", order, TimeSpan.FromHours(2), Now.AddDays(1));

        Assert.True(updated.IsSuccess);
        Assert.Equal("Outra Clinica", settings.DisplayName);
        Assert.Equal("America/Manaus", settings.TimeZoneId);
        Assert.Equal(order, settings.ChannelOrder);
        Assert.Equal(TimeSpan.FromHours(2), settings.ReminderLead);
        Assert.Equal(Now.AddDays(1), settings.UpdatedAt);
    }

    [Fact]
    public void Update_Invalid_KeepsPreviousValues()
    {
        var settings = Clinic();

        var updated = settings.Update("Outra", "Invalida/Zona", ChannelOrder.Default, TimeSpan.FromHours(2), Now);

        Assert.True(updated.IsFailure);
        Assert.Equal("Clinica Exemplo", settings.DisplayName);
        Assert.Throws<ArgumentNullException>(() => settings.Update("x", "y", null!, TimeSpan.Zero, Now));
    }

    [Fact]
    public void Defaults_UseWhatsAppSmsEmailAnd24Hours()
    {
        var defaults = ClinicSettings.Defaults(Tenant, "sua clínica");

        Assert.Equal([Channel.WhatsApp, Channel.Sms, Channel.Email], defaults.ChannelOrder.Channels);
        Assert.Equal(TimeSpan.FromHours(24), defaults.ReminderLead);
        Assert.Equal(ClinicSettings.DefaultTimeZone, defaults.TimeZoneId);
    }
}
