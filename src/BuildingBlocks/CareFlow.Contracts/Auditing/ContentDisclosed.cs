namespace CareFlow.Contracts.Auditing;

/// <summary>
/// Um serviço enviou conteúdo para fora do CareFlow (por exemplo, a queixa pseudonimizada ao provedor de IA). Vai
/// para a trilha de auditoria como um <see cref="DataAccessRecorded"/>, mais o <see cref="ContentDigest"/>: o SHA-256
/// (hexadecimal minúsculo) do conteúdo exato que saiu. A trilha guarda só o resumo; o conteúdo fica no serviço de
/// origem, que pode apagá-lo a pedido do titular sem quebrar a cadeia.
/// </summary>
public sealed record ContentDisclosed(
    string SourceService,
    string Actor,
    string Action,
    string ResourceType,
    Guid ResourceId,
    string ContentDigest,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public static string EventType => "audit.content-disclosed";
}
