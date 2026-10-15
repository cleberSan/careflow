namespace CareFlow.Audit.Domain.Chain;

internal static class AuditTime
{
    /// <summary>
    /// Trunca para microssegundos, a precisão do <c>timestamptz</c> do PostgreSQL. Sem isso, o hash calculado
    /// antes de gravar não bateria com o recalculado a partir do valor lido do banco.
    /// </summary>
    public static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
