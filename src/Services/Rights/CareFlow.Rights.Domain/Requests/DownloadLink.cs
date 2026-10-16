namespace CareFlow.Rights.Domain.Requests;

/// <summary>
/// Link de download do pacote: curto (15 minutos), de uso único e auditado. Só o SHA-256 do token fica no banco;
/// o token sai uma vez, na criação.
/// </summary>
public sealed class DownloadLink
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private DownloadLink()
    {
        // Usado pelo EF Core.
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RequestId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public static DownloadLink Create(Guid tenantId, Guid requestId, byte[] tokenHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        var createdAt = ToStoragePrecision(now);
        return new DownloadLink
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            RequestId = requestId,
            TokenHash = tokenHash,
            CreatedAt = createdAt,
            ExpiresAt = createdAt + Lifetime,
        };
    }

    /// <summary>Usa o link. Falso se já foi usado ou expirou; o chamador responde igual nos dois casos.</summary>
    public bool TryRedeem(DateTimeOffset now)
    {
        if (UsedAt is not null || now >= ExpiresAt)
        {
            return false;
        }

        UsedAt = ToStoragePrecision(now);
        return true;
    }

    private static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
