using CareFlow.Audit.Domain.Chain;

namespace CareFlow.Audit.Api.Infrastructure.Persistence;

/// <summary>Linha da tabela append-only <c>audit_entries</c>. Só é inserida e lida, nunca alterada.</summary>
internal sealed class AuditEntryRecord
{
    public Guid TenantId { get; init; }

    public long Sequence { get; init; }

    public Guid MessageId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset RecordedAt { get; init; }

    public string SourceService { get; init; } = string.Empty;

    public string Actor { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string ResourceType { get; init; } = string.Empty;

    public Guid ResourceId { get; init; }

    /// <summary>SHA-256 (32 bytes) do conteúdo enviado para fora pelo serviço de origem, quando houver.</summary>
    public byte[]? ContentDigest { get; init; }

    public string KeyId { get; init; } = string.Empty;

    public byte[] PreviousHash { get; init; } = [];

    public byte[] Hash { get; init; } = [];

    public string? ContentDigestHex => ContentDigest is null ? null : Convert.ToHexStringLower(ContentDigest);

    public static AuditEntryRecord From(ChainedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AuditEntryRecord
        {
            TenantId = entry.Event.TenantId,
            Sequence = entry.Sequence,
            MessageId = entry.Event.MessageId,
            OccurredAt = entry.Event.OccurredAt,
            RecordedAt = entry.RecordedAt,
            SourceService = entry.Event.SourceService,
            Actor = entry.Event.Actor,
            Action = entry.Event.Action,
            ResourceType = entry.Event.ResourceType,
            ResourceId = entry.Event.ResourceId,
            ContentDigest = entry.Event.ContentDigest is { } digest ? Convert.FromHexString(digest) : null,
            KeyId = entry.KeyId,
            PreviousHash = entry.PreviousHash,
            Hash = entry.Hash,
        };
    }

    /// <summary>
    /// Reconstrói a entrada para verificação. Uma linha adulterada com valores que o domínio não aceitaria
    /// (por exemplo, ator com quebra de linha) não reconstrói e conta como hash inválido.
    /// </summary>
    public ChainedEntry? ToChainedEntry()
    {
        var auditEvent = AuditEvent.Create(
            TenantId, MessageId, OccurredAt, SourceService, Actor, Action, ResourceType, ResourceId, ContentDigestHex);
        return auditEvent.IsSuccess
            ? new ChainedEntry(Sequence, auditEvent.Value, RecordedAt, KeyId, PreviousHash, Hash)
            : null;
    }
}
