namespace CareFlow.Audit.Api.Infrastructure.Persistence;

/// <summary>
/// Cabeça da cadeia de uma clínica. A linha é travada (<c>FOR UPDATE</c>) a cada gravação, o que serializa
/// as gravações da mesma clínica sem bloquear as outras. Também é o que denuncia a remoção das últimas entradas.
/// </summary>
internal sealed class ChainHeadRecord
{
    public Guid TenantId { get; set; }

    public long Sequence { get; set; }

    public byte[] Hash { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
