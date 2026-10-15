using System.Buffers.Binary;

namespace CareFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// UUID v7 estritamente crescente dentro do processo. O v7 padrão só ordena por milissegundo; dois eventos da
/// mesma transação teriam ordem aleatória. Aqui, se o novo id não for maior que o anterior, usa o anterior + 1,
/// então o despachante publica na ordem em que os eventos foram criados.
/// </summary>
internal static class MonotonicGuid
{
    private static readonly Lock Gate = new();
    private static UInt128 _last;

    public static Guid Next(DateTimeOffset now)
    {
        var candidate = ToUInt128(Guid.CreateVersion7(now));
        lock (Gate)
        {
            _last = candidate > _last ? candidate : _last + 1;
            return FromUInt128(_last);
        }
    }

    private static UInt128 ToUInt128(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }

    private static Guid FromUInt128(UInt128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
        return new Guid(bytes, bigEndian: true);
    }
}
