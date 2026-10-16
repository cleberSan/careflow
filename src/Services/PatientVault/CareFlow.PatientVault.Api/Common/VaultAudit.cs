using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;

namespace CareFlow.PatientVault.Api.Common;

/// <summary>
/// Registro de acesso do cofre para a trilha de auditoria. Vai pelo Outbox, na mesma transação da operação
/// (ou, nas leituras, numa gravação logo depois da leitura). Só o pseudônimo sai do cofre.
/// </summary>
internal static class VaultAudit
{
    public const string SourceService = "patient-vault";
    public const string ResourceType = "patient";

    public const string Registered = "patient.registered";
    public const string Read = "patient.read";
    public const string Updated = "patient.updated";
    public const string LookedUp = "patient.lookup";
    public const string ContactRead = "patient.contact.read";
    public const string TriageProfileRead = "patient.triage-profile.read";
    public const string Exported = "patient.exported";
    public const string Erased = "patient.erased";
    public const string ErasureReapplied = "patient.erasure-reapplied";

    /// <summary>Ator das ações feitas em resposta a um pedido do titular (o pedido em si fica no serviço de direitos).</summary>
    public const string RightsActor = "careflow-rights";

    // Operações na chave da clínica: o recurso é a própria clínica (o id é o do tenant).
    public const string TenantKeyResource = "tenant-key";
    public const string TenantKeyRotated = "tenant-key.rotated";
    public const string TenantKeyDestroyed = "tenant-key.destroyed";

    public static void Record(this VaultDbContext db, Guid tenantId, Guid patientRef, string actor, string action, DateTimeOffset now) =>
        db.RecordAccess(tenantId, SourceService, actor, action, ResourceType, patientRef, now);

    public static void Record(this VaultDbContext db, Guid tenantId, Guid resourceId, string actor, string action, string resourceType, DateTimeOffset now) =>
        db.RecordAccess(tenantId, SourceService, actor, action, resourceType, resourceId, now);
}
