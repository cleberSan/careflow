using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Rights.Api.Infrastructure.Persistence;

namespace CareFlow.Rights.Api.Common;

/// <summary>
/// Registros do serviço de pedidos na trilha de auditoria, pelo Outbox. O recurso é o paciente (pseudônimo): a
/// trilha do titular mostra o pedido, a confirmação de identidade e cada download junto com o resto.
/// </summary>
internal static class RightsAudit
{
    public const string SourceService = "rights";
    public const string ResourceType = "patient";

    /// <summary>Quem usa o link de download: o titular, sem conta no sistema.</summary>
    public const string SubjectActor = "titular-via-link";

    public const string SystemActor = "careflow-rights";

    public const string Received = "subject-request.received";
    public const string IdentityConfirmed = "subject-request.identity-confirmed";
    public const string IdentityRejected = "subject-request.identity-rejected";
    public const string Denied = "subject-request.denied";
    public const string Completed = "subject-request.completed";
    public const string DownloadLinkCreated = "subject-request.download-link-created";
    public const string PackageDownloaded = "subject-request.package-downloaded";
    public const string ErasureReapplied = "subject-request.erasure-reapplied";

    public static void Record(this RightsDbContext db, Guid tenantId, Guid patientRef, string actor, string action, DateTimeOffset now) =>
        db.RecordAccess(tenantId, SourceService, actor, action, ResourceType, patientRef, now);
}
