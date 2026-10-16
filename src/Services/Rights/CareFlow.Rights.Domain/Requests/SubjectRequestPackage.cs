using System.Security.Cryptography;

namespace CareFlow.Rights.Domain.Requests;

/// <summary>
/// Pacote de acesso ou portabilidade: um ZIP cifrado com uma chave só dele, que fica cifrada pela chave do serviço.
/// Guardado por até 7 dias (decisão de Cleber, ADR-0012); ao expirar, chave e conteúdo são apagados.
/// </summary>
public sealed class SubjectRequestPackage
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private SubjectRequestPackage()
    {
        // Usado pelo EF Core.
    }

    public Guid RequestId { get; private set; }

    public Guid TenantId { get; private set; }

    public byte[]? Content { get; private set; }

    public byte[]? WrappedKey { get; private set; }

    public long Size { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? DestroyedAt { get; private set; }

    public bool IsAvailable(DateTimeOffset now) => DestroyedAt is null && now < ExpiresAt && Content is not null && WrappedKey is not null;

    public static SubjectRequestPackage Create(Guid tenantId, Guid requestId, byte[] content, byte[] wrappedKey, long size, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(wrappedKey);
        var createdAt = ToStoragePrecision(now);
        return new SubjectRequestPackage
        {
            RequestId = requestId,
            TenantId = tenantId,
            Content = content,
            WrappedKey = wrappedKey,
            Size = size,
            CreatedAt = createdAt,
            ExpiresAt = createdAt + Retention,
        };
    }

    /// <summary>Apaga chave e conteúdo. Repetir não muda nada.</summary>
    public void Destroy(DateTimeOffset now)
    {
        if (DestroyedAt is not null)
        {
            return;
        }

        if (WrappedKey is not null)
        {
            CryptographicOperations.ZeroMemory(WrappedKey);
        }

        WrappedKey = null;
        Content = null;
        DestroyedAt = ToStoragePrecision(now);
    }

    private static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
