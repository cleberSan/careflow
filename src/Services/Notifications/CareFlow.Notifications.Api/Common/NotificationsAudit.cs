using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Notifications;

namespace CareFlow.Notifications.Api.Common;

/// <summary>
/// Como o Notifications aparece na trilha de auditoria. O recurso é o paciente (pseudônimo); a ação diz o tipo
/// de aviso e o resultado, sem texto da mensagem nem contato.
/// </summary>
internal static class NotificationsAudit
{
    public const string SourceService = "notifications";
    public const string ResourceType = "patient";

    /// <summary>Ator dos envios, que são sempre automáticos.</summary>
    public const string SystemActor = "careflow-notifications";

    public static string SentAction(NotificationKind kind, Channel channel) => $"notification.{Slug(kind)}.sent.{channel.Slug()}";

    public static string NotSentAction(NotificationKind kind, NotSentReason reason) =>
        $"notification.{Slug(kind)}.not-sent.{(reason == NotSentReason.NoConsent ? "no-consent" : "no-contact")}";

    public static string FailedAction(NotificationKind kind) => $"notification.{Slug(kind)}.failed";

    public static void Record(this NotificationsDbContext db, Notification notification, string action, DateTimeOffset now) =>
        db.RecordAccess(notification.TenantId, SourceService, SystemActor, action, ResourceType, notification.PatientRef, now);

    private static string Slug(NotificationKind kind) => kind switch
    {
        NotificationKind.Confirmation => "confirmation",
        NotificationKind.Reminder => "reminder",
        _ => "cancellation",
    };
}
