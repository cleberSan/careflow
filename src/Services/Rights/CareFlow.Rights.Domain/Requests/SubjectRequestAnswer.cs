namespace CareFlow.Rights.Domain.Requests;

/// <summary>
/// Resposta de um serviço ao pedido. <see cref="SealedSection"/> é a seção cifrada para o pedido (apagada quando o
/// pacote fica pronto); <see cref="Erasure"/> é o JSON das linhas do comprovante, só com contagens.
/// </summary>
public sealed class SubjectRequestAnswer
{
    private SubjectRequestAnswer()
    {
        // Usado pelo EF Core.
    }

    public Guid RequestId { get; private set; }

    public string Service { get; private set; } = string.Empty;

    public Guid TenantId { get; private set; }

    public string Outcome { get; private set; } = string.Empty;

    public string? SealedSection { get; private set; }

    public string? Erasure { get; private set; }

    public DateTimeOffset AnsweredAt { get; private set; }

    public static SubjectRequestAnswer Create(
        Guid tenantId, Guid requestId, string service, string outcome, string? sealedSection, string? erasure, DateTimeOffset answeredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        var utc = answeredAt.ToUniversalTime();
        return new SubjectRequestAnswer
        {
            TenantId = tenantId,
            RequestId = requestId,
            Service = service,
            Outcome = outcome,
            SealedSection = sealedSection,
            Erasure = erasure,
            AnsweredAt = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond)),
        };
    }

    /// <summary>Depois de montado o pacote a seção não abre mais (a chave do pedido foi destruída); sai do banco.</summary>
    public void DiscardSection() => SealedSection = null;
}
