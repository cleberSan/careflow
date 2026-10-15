using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Audit.Domain.Chain;

/// <summary>
/// Um acesso ou alteração a registrar: quem (<see cref="Actor"/>), o quê (<see cref="Action"/> sobre
/// <see cref="ResourceType"/>/<see cref="ResourceId"/>), quando e em qual serviço. Nunca carrega dados
/// pessoais do paciente, só o pseudônimo no <see cref="ResourceId"/>.
/// </summary>
public sealed record AuditEvent
{
    public const int MaxCodeLength = 64;
    public const int MaxActorLength = 128;

    private AuditEvent(
        Guid tenantId,
        Guid messageId,
        DateTimeOffset occurredAt,
        string sourceService,
        string actor,
        string action,
        string resourceType,
        Guid resourceId)
    {
        TenantId = tenantId;
        MessageId = messageId;
        OccurredAt = occurredAt;
        SourceService = sourceService;
        Actor = actor;
        Action = action;
        ResourceType = resourceType;
        ResourceId = resourceId;
    }

    public Guid TenantId { get; }

    /// <summary>Identificador da mensagem de origem; garante que um evento reentregue não vire duas entradas.</summary>
    public Guid MessageId { get; }

    public DateTimeOffset OccurredAt { get; }

    public string SourceService { get; }

    /// <summary>Identificador do usuário no provedor de identidade (claim <c>sub</c>), ou o nome do serviço.</summary>
    public string Actor { get; }

    public string Action { get; }

    public string ResourceType { get; }

    public Guid ResourceId { get; }

    public static Result<AuditEvent> Create(
        Guid tenantId,
        Guid messageId,
        DateTimeOffset occurredAt,
        string? sourceService,
        string? actor,
        string? action,
        string? resourceType,
        Guid resourceId)
    {
        if (tenantId == Guid.Empty || messageId == Guid.Empty || resourceId == Guid.Empty)
        {
            return AuditErrors.MissingIdentifier;
        }

        if (!IsCode(sourceService) || !IsCode(action) || !IsCode(resourceType))
        {
            return AuditErrors.InvalidCode;
        }

        if (!IsActor(actor))
        {
            return AuditErrors.InvalidActor;
        }

        return new AuditEvent(
            tenantId,
            messageId,
            AuditTime.ToStoragePrecision(occurredAt),
            sourceService!,
            actor!,
            action!,
            resourceType!,
            resourceId);
    }

    private static bool IsCode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxCodeLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    // Só ASCII visível: impede quebras de linha e caracteres de controle que forjariam linhas em relatórios e logs.
    private static bool IsActor(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxActorLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
