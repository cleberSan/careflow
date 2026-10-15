using System.Diagnostics;

namespace CareFlow.Notifications.Api.Common.Observability;

/// <summary>
/// Spans do Notifications: um por aviso processado e um por tentativa de canal, com fallback e mudanças do
/// circuit breaker como eventos. Tags só com canal, tipo e resultado; nunca contato, pseudônimo ou clínica.
/// </summary>
internal static class NotificationsDiagnostics
{
    public const string SourceName = "CareFlow.Notifications";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>
    /// Continua o trace de quem agendou o aviso (a reserva ou o cancelamento que chegou pelo broker), para que a
    /// reserva apareça como um trace só, da API da agenda ao aviso enviado. Sem contexto gravado, começa um novo.
    /// </summary>
    public static Activity? StartDelivery(string? traceParent)
    {
        ActivityContext.TryParse(traceParent, null, out var parent);
        return ActivitySource.StartActivity("deliver notification", ActivityKind.Internal, parent);
    }

    public static Activity? StartChannelAttempt(string channel)
    {
        var activity = ActivitySource.StartActivity($"send {channel}", ActivityKind.Client);
        activity?.SetTag("careflow.notification.channel", channel);
        return activity;
    }
}
