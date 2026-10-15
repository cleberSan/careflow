using System.Text.Json;

namespace CareFlow.BuildingBlocks.Messaging;

internal static class EventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<TEvent>(TEvent @event) => JsonSerializer.Serialize(@event, Options);

    public static TEvent? Deserialize<TEvent>(ReadOnlySpan<byte> utf8Json) => JsonSerializer.Deserialize<TEvent>(utf8Json, Options);
}
