using System.Diagnostics;

namespace CareFlow.Notifications.Api.Infrastructure.Persistence;

/// <summary>
/// Entrada da fila de envios: quando o aviso deve ser processado e até quando está reservado por uma instância.
/// Tabela separada e enxuta para que o papel do despachante, que enxerga todas as clínicas, só veja
/// identificadores e horários, nunca os avisos.
/// </summary>
internal sealed class NotificationJob
{
    public Guid NotificationId { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>
    /// Contexto W3C de quem agendou o aviso (o consumo do evento da agenda), para o envio continuar o mesmo
    /// trace. Só identificadores de trace, nenhum dado do aviso.
    /// </summary>
    public string? TraceParent { get; set; }

    public static NotificationJob For(Domain.Notifications.Notification notification) =>
        new()
        {
            NotificationId = notification.Id,
            TenantId = notification.TenantId,
            DueAt = notification.DueAt,
            TraceParent = Activity.Current?.Id,
        };
}
