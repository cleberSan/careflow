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

    public static void Record(this VaultDbContext db, Guid tenantId, Guid patientRef, string actor, string action, DateTimeOffset now) =>
        db.RecordAccess(tenantId, SourceService, actor, action, ResourceType, patientRef, now);
}
