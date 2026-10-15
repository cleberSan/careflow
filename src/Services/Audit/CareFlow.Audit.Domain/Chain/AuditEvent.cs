using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Audit.Domain.Chain;

/// <summary>
/// Um acesso ou alteração a registrar: quem (<see cref="Actor"/>), o quê (<see cref="Action"/> sobre
/// <see cref="ResourceType"/>/<see cref="ResourceId"/>), quando e em qual serviço. Nunca carrega dados
/// pessoais do paciente, só o pseudônimo no <see cref="ResourceId"/>. Quando o serviço precisa provar o que
/// enviou para fora (a triagem, por exemplo), <see cref="ContentDigest"/> leva só o SHA-256 desse conteúdo.
/// </summary>
public sealed record AuditEvent
{
    public const int MaxCodeLength = 64;
    public const int MaxActorLength = 128;
    public const int ContentDigestSize = 32;

    private AuditEvent(
        Guid tenantId,
        Guid messageId,
        DateTimeOffset occurredAt,
        string sourceService,
        string actor,
        string action,
        string resourceType,
        Guid resourceId,
        string? contentDigest)
    {
        TenantId = tenantId;
        MessageId = messageId;
        OccurredAt = occurredAt;
        SourceService = sourceService;
        Actor = actor;
        Action = action;
        ResourceType = resourceType;
        ResourceId = resourceId;
        ContentDigest = contentDigest;
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

    /// <summary>
    /// SHA-256, em hexadecimal minúsculo, do conteúdo que o serviço enviou para fora, quando houver. Entra no
    /// hash encadeado. É só o resumo: o conteúdo em si fica no serviço de origem, que pode apagá-lo.
    /// </summary>
    public string? ContentDigest { get; }

    public static Result<AuditEvent> Create(
        Guid tenantId,
        Guid messageId,
        DateTimeOffset occurredAt,
        string? sourceService,
        string? actor,
        string? action,
        string? resourceType,
        Guid resourceId,
        string? contentDigest = null)
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

        if (contentDigest is not null && !IsDigest(contentDigest))
        {
            return AuditErrors.InvalidContentDigest;
        }

        return new AuditEvent(
            tenantId,
            messageId,
            AuditTime.ToStoragePrecision(occurredAt),
            sourceService!,
            actor!,
            action!,
            resourceType!,
            resourceId,
            contentDigest);
    }

    // Exatamente 64 dígitos hexadecimais minúsculos: uma forma só por valor, sem ambiguidade na cadeia.
    private static bool IsDigest(string value)
    {
        if (value.Length != ContentDigestSize * 2)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
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
