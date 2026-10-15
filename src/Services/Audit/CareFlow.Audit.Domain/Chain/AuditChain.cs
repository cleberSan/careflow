using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CareFlow.Audit.Domain.Chain;

/// <summary>
/// Hash encadeado da auditoria: <c>hash(n) = HMAC-SHA256(chave, v1 ‖ campos(n) ‖ hash(n-1))</c>, com
/// codificação binária canônica (campos de texto prefixados pelo tamanho), sem ambiguidade de separador.
/// </summary>
public static class AuditChain
{
    public const int HashSize = 32;

    private static readonly byte[] DomainSeparator = Encoding.ASCII.GetBytes("careflow-audit-v1");

    /// <summary>Monta a próxima entrada a partir da cabeça atual da cadeia.</summary>
    public static ChainedEntry Append(ChainHead head, AuditEvent auditEvent, DateTimeOffset recordedAt, ChainKey key)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(key);

        var sequence = checked(head.Sequence + 1);
        var recorded = AuditTime.ToStoragePrecision(recordedAt);
        var hash = ComputeHash(sequence, auditEvent, recorded, head.Hash, key);
        return new ChainedEntry(sequence, auditEvent, recorded, key.Id, head.Hash.ToArray(), hash);
    }

    public static byte[] ComputeHash(
        long sequence,
        AuditEvent auditEvent,
        DateTimeOffset recordedAt,
        ReadOnlySpan<byte> previousHash,
        ChainKey key)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(key);
        if (previousHash.Length != HashSize)
        {
            throw new ArgumentException($"O hash anterior precisa ter {HashSize} bytes.", nameof(previousHash));
        }

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.Secret);
        hmac.AppendData(DomainSeparator);
        AppendGuid(hmac, auditEvent.TenantId);
        AppendInt64(hmac, sequence);
        AppendGuid(hmac, auditEvent.MessageId);
        AppendInt64(hmac, auditEvent.OccurredAt.UtcTicks);
        AppendInt64(hmac, recordedAt.UtcTicks);
        AppendText(hmac, auditEvent.SourceService);
        AppendText(hmac, auditEvent.Actor);
        AppendText(hmac, auditEvent.Action);
        AppendText(hmac, auditEvent.ResourceType);
        AppendGuid(hmac, auditEvent.ResourceId);
        hmac.AppendData(previousHash);
        return hmac.GetHashAndReset();
    }

    private static void AppendGuid(IncrementalHash hash, Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        value.TryWriteBytes(buffer, bigEndian: true, out _);
        hash.AppendData(buffer);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
