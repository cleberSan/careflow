using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Clinics;

namespace CareFlow.Notifications.UnitTests.Notifications;

public sealed class ChannelOrderTests
{
    [Fact]
    public void Create_KeepsOrder()
    {
        var order = ChannelOrder.Create([Channel.Sms, Channel.WhatsApp]);

        Assert.True(order.IsSuccess);
        Assert.Equal([Channel.Sms, Channel.WhatsApp], order.Value.Channels);
        Assert.Equal("Sms,WhatsApp", order.Value.ToString());
    }

    [Fact]
    public void Create_RejectsEmptyRepeatedOrUnknown()
    {
        Assert.Equal(ClinicErrors.InvalidChannelOrder, ChannelOrder.Create(null).Error);
        Assert.Equal(ClinicErrors.InvalidChannelOrder, ChannelOrder.Create([]).Error);
        Assert.Equal(ClinicErrors.InvalidChannelOrder, ChannelOrder.Create([Channel.Sms, Channel.Sms]).Error);
        Assert.Equal(ClinicErrors.InvalidChannelOrder, ChannelOrder.Create([(Channel)99]).Error);
    }

    [Fact]
    public void Parse_RoundTripsAndRejectsGarbage()
    {
        Assert.Equal(ChannelOrder.Default, ChannelOrder.Parse(ChannelOrder.Default.ToString()));
        Assert.Throws<FormatException>(() => ChannelOrder.Parse("Sms,Sms"));
        Assert.Throws<ArgumentException>(() => ChannelOrder.Parse("Pombo"));
        Assert.Throws<ArgumentException>(() => ChannelOrder.Parse(" "));
    }

    [Fact]
    public void Equality_IsByContentAndOrder()
    {
        var a = ChannelOrder.Create([Channel.Email, Channel.Sms]).Value;
        var b = ChannelOrder.Create([Channel.Email, Channel.Sms]).Value;
        var c = ChannelOrder.Create([Channel.Sms, Channel.Email]).Value;

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.False(a.Equals(null));
        Assert.False(a.Equals((object)"Email,Sms"));
    }

    [Theory]
    [InlineData(Channel.WhatsApp, "whatsapp", true)]
    [InlineData(Channel.Sms, "sms", true)]
    [InlineData(Channel.Email, "email", false)]
    public void Channel_SlugAndContactKind(Channel channel, string slug, bool usesPhone)
    {
        Assert.Equal(slug, channel.Slug());
        Assert.Equal(usesPhone, channel.UsesPhone());
    }

    [Fact]
    public void Slug_OfUnknownChannel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Channel)42).Slug());
    }
}
