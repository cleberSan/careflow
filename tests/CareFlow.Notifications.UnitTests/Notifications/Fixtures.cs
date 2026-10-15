using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Clinics;
using CareFlow.Notifications.Domain.Notifications;

namespace CareFlow.Notifications.UnitTests.Notifications;

internal static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    // 20/11/2026 13:30 UTC = 10:30 em São Paulo (UTC-3, sem horário de verão).
    public static readonly DateTimeOffset StartsAt = new(2026, 11, 20, 13, 30, 0, TimeSpan.Zero);

    public static Notification Confirmation(DateTimeOffset? startsAt = null) =>
        Notification.Confirmation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), startsAt ?? StartsAt, Now);

    public static Notification Cancellation(CancellationCause cause = CancellationCause.Requested) =>
        Notification.Cancellation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StartsAt, cause, Now);

    public static ClinicSettings Clinic(string name = "Clinica Exemplo") =>
        ClinicSettings.Create(Guid.NewGuid(), name, "America/Sao_Paulo", ChannelOrder.Default, TimeSpan.FromHours(24), Now).Value;
}
