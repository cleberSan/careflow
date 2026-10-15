using CareFlow.Notifications.Domain.Messages;
using CareFlow.Notifications.Domain.Notifications;

using static CareFlow.Notifications.UnitTests.Notifications.Fixtures;

namespace CareFlow.Notifications.UnitTests.Notifications;

public sealed class MessageComposerTests
{
    [Fact]
    public void Confirmation_HasLocalDateTimeAndClinicOnly()
    {
        var text = MessageComposer.Compose(Confirmation(), Clinic());

        Assert.Equal("CareFlow: sua consulta está marcada para 20/11/2026 às 10:30 em Clinica Exemplo.", text);
    }

    [Fact]
    public void Reminder_UsesReminderText()
    {
        var reminder = Notification.Reminder(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StartsAt, TimeSpan.FromHours(24), Now)!;

        var text = MessageComposer.Compose(reminder, Clinic());

        Assert.StartsWith("CareFlow: lembrete da sua consulta em 20/11/2026 às 10:30", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_ExplainsConsentRevocationWithoutHealthData()
    {
        var requested = MessageComposer.Compose(Cancellation(CancellationCause.Requested), Clinic());
        var revoked = MessageComposer.Compose(Cancellation(CancellationCause.ConsentRevoked), Clinic());

        Assert.Contains("foi cancelada. Para reagendar", requested, StringComparison.Ordinal);
        Assert.DoesNotContain("consentimento", requested, StringComparison.Ordinal);
        Assert.Contains("consentimento para agendamento foi revogado", revoked, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_FitsInOneLongSmsAndNeverCarriesIdentifiers()
    {
        var notification = Cancellation(CancellationCause.ConsentRevoked);
        var text = MessageComposer.Compose(notification, Clinic(new string('a', 60)));

        Assert.True(text.Length <= 320);
        Assert.DoesNotContain(notification.PatientRef.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(notification.AppointmentId.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compose_RequiresArguments()
    {
        Assert.Throws<ArgumentNullException>(() => MessageComposer.Compose(null!, Clinic()));
        Assert.Throws<ArgumentNullException>(() => MessageComposer.Compose(Confirmation(), null!));
    }
}
