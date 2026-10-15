namespace CareFlow.Contracts.Auditing;

/// <summary>
/// Alguém leu ou alterou um dado. Vai para a trilha de auditoria. <see cref="Actor"/> é o <c>sub</c> do token
/// (ou o nome do serviço, em ações automáticas); <see cref="ResourceId"/> é um pseudônimo ou identificador técnico.
/// </summary>
public sealed record DataAccessRecorded(
    string SourceService,
    string Actor,
    string Action,
    string ResourceType,
    Guid ResourceId,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public static string EventType => "audit.data-access";
}
