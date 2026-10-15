using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Notifications.Domain.Channels;

/// <summary>
/// Ordem de tentativa dos canais de uma clínica: o primeiro é o preferido, os seguintes são o fallback.
/// Sem repetição e com pelo menos um canal.
/// </summary>
public sealed class ChannelOrder : IEquatable<ChannelOrder>
{
    private readonly Channel[] _channels;

    private ChannelOrder(Channel[] channels)
    {
        _channels = channels;
    }

    public static ChannelOrder Default { get; } = new([Channel.WhatsApp, Channel.Sms, Channel.Email]);

    public IReadOnlyList<Channel> Channels => _channels;

    public static Result<ChannelOrder> Create(IEnumerable<Channel>? channels)
    {
        var list = channels?.ToArray() ?? [];
        if (list.Length == 0 || list.Length != list.Distinct().Count() || list.Any(c => !Enum.IsDefined(c)))
        {
            return Clinics.ClinicErrors.InvalidChannelOrder;
        }

        return new ChannelOrder(list);
    }

    /// <summary>Lê o formato gravado no banco (<c>WhatsApp,Sms,Email</c>).</summary>
    public static ChannelOrder Parse(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        var channels = stored.Split(',').Select(value => Enum.Parse<Channel>(value, ignoreCase: false));
        var order = Create(channels);
        return order.IsSuccess ? order.Value : throw new FormatException("Ordem de canais gravada é inválida.");
    }

    public override string ToString() => string.Join(',', _channels);

    public bool Equals(ChannelOrder? other) => other is not null && _channels.SequenceEqual(other._channels);

    public override bool Equals(object? obj) => Equals(obj as ChannelOrder);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var channel in _channels)
        {
            hash.Add(channel);
        }

        return hash.ToHashCode();
    }
}
