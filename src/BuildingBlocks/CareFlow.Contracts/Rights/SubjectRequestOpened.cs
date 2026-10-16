namespace CareFlow.Contracts.Rights;

/// <summary>
/// Identidade confirmada: cada serviço de <see cref="SubjectRequestServices.All"/> responde com
/// <see cref="SubjectRequestAnswered"/>. Em acesso e portabilidade, <see cref="RecipientPublicKey"/> é a chave
/// pública (P-256, SPKI em Base64) só deste pedido, para a qual cada serviço cifra a própria seção. Em eliminação
/// ela é nula, e <see cref="PolicyVersion"/> é a política de retenção que cada serviço aplica.
/// </summary>
public sealed record SubjectRequestOpened(
    Guid RequestId,
    Guid PatientRef,
    string Kind,
    string? RecipientPublicKey,
    string PolicyVersion,
    DateTimeOffset OpenedAt) : IIntegrationEvent
{
    public static string EventType => "rights.subject-request-opened";
}

/// <summary>
/// Resposta de um serviço ao pedido. <see cref="SealedSection"/> é a seção do pacote (JSON) cifrada para o pedido,
/// só em acesso e portabilidade. <see cref="Erasure"/> lista, por item da política, o que aconteceu.
/// </summary>
public sealed record SubjectRequestAnswered(
    Guid RequestId,
    string Service,
    string Outcome,
    string? SealedSection,
    IReadOnlyList<ErasureLine> Erasure,
    DateTimeOffset AnsweredAt) : IIntegrationEvent
{
    public static string EventType => "rights.subject-request-answered";
}

/// <summary>Um item da política aplicado a este paciente, com quantos registros foram afetados.</summary>
public sealed record ErasureLine(string Item, string Action, int Records);

/// <summary>
/// Reaplica uma eliminação já concluída, depois de uma restauração de backup: o banco restaurado pode ter de volta
/// a chave do paciente ou o texto da queixa. Cada serviço trata como uma eliminação nova, de forma idempotente.
/// </summary>
public sealed record ErasureReapplied(
    Guid RequestId,
    Guid PatientRef,
    string PolicyVersion,
    DateTimeOffset ErasedAt) : IIntegrationEvent
{
    public static string EventType => "rights.erasure-reapplied";
}
