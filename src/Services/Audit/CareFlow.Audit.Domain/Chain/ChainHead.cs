namespace CareFlow.Audit.Domain.Chain;

/// <summary>Última posição conhecida da cadeia de uma clínica.</summary>
public sealed record ChainHead(long Sequence, byte[] Hash)
{
    /// <summary>Cadeia vazia: a primeira entrada aponta para 32 bytes zerados.</summary>
    public static ChainHead Genesis { get; } = new(0, new byte[AuditChain.HashSize]);
}
