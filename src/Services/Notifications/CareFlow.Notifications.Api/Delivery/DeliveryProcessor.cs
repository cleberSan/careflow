using System.Diagnostics;
using System.Security.Cryptography;

using CareFlow.Notifications.Api.Channels;
using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Api.Common;
using CareFlow.Notifications.Api.Common.Observability;
using CareFlow.Notifications.Api.Contacts;
using CareFlow.Notifications.Api.Features;
using CareFlow.Notifications.Api.Infrastructure;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Messages;
using CareFlow.Notifications.Domain.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Delivery;

internal enum ProcessingOutcome
{
    Sent = 1,
    Closed,
    Rescheduled,
    Gone,
}

/// <summary>
/// Processa um aviso da fila, num escopo com a clínica dele (RLS). Confere as regras na hora (consulta
/// cancelada ou começada, consentimento), busca o contato, envia pelos canais em ordem e grava o resultado,
/// as tentativas e o registro de auditoria juntos. O envio acontece fora de transação: nenhuma conexão fica
/// presa esperando o provedor.
/// </summary>
internal sealed partial class DeliveryProcessor(
    NotificationsDbContext db,
    IContactDirectory contacts,
    ChannelRouter router,
    ClinicFaultResolver faults,
    NotificationsMetrics metrics,
    TimeProvider time,
    IOptions<NotificationOptions> options,
    ILogger<DeliveryProcessor> logger)
{
    public async Task<ProcessingOutcome> ProcessAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var settings = options.Value;

        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == job.NotificationId, cancellationToken).ConfigureAwait(false);
        Activity.Current?.SetTag("careflow.notification.kind", notification?.Kind.ToString().ToLowerInvariant());
        if (notification is null)
        {
            await ReleaseAsync(job, reschedule: null, cancellationToken).ConfigureAwait(false);
            return ProcessingOutcome.Gone;
        }

        var now = time.GetUtcNow();
        var appointment = await db.Appointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AppointmentId == notification.AppointmentId, cancellationToken).ConfigureAwait(false);
        var consent = await db.Consents.AsNoTracking()
            .FirstOrDefaultAsync(c => c.PatientRef == notification.PatientRef, cancellationToken).ConfigureAwait(false);

        switch (DeliveryRules.Evaluate(notification, appointment, consent, settings.ConsentGracePeriod, now))
        {
            case DeliveryDecision.AlreadyClosed:
                await ReleaseAsync(job, reschedule: null, cancellationToken).ConfigureAwait(false);
                return ProcessingOutcome.Closed;

            case DeliveryDecision.Supersede:
                notification.Supersede(now);
                return await CloseAsync(job, notification, "superseded", auditAction: null, cancellationToken).ConfigureAwait(false);

            case DeliveryDecision.Expire:
                notification.Expire(now);
                return await CloseAsync(job, notification, "expired", auditAction: null, cancellationToken).ConfigureAwait(false);

            case DeliveryDecision.BlockNoConsent:
                return await NotSentAsync(job, notification, NotSentReason.NoConsent, now, cancellationToken).ConfigureAwait(false);

            case DeliveryDecision.WaitForConsent:
                var waitUntil = Min(notification.CreatedAt + settings.ConsentGracePeriod, now + settings.RoundRetryDelay);
                await ReleaseAsync(job, waitUntil, cancellationToken).ConfigureAwait(false);
                return ProcessingOutcome.Rescheduled;
        }

        var lookup = await contacts.FindAsync(notification.TenantId, notification.PatientRef, cancellationToken).ConfigureAwait(false);
        var noContact = lookup.Status == ContactLookupStatus.NotFound
            || (lookup.Status == ContactLookupStatus.Found && lookup.Contact is null or { IsEmpty: true });
        if (noContact)
        {
            return await NotSentAsync(job, notification, NotSentReason.NoContact, now, cancellationToken).ConfigureAwait(false);
        }

        var round = notification.Rounds + 1;
        RoutingResult routing;
        if (lookup.Status == ContactLookupStatus.Unavailable)
        {
            LogContactUnavailable(logger, notification.Id);
            routing = new RoutingResult([], null, null);
        }
        else
        {
            var clinic = await ProjectionStore.ClinicSettingsAsync(db, notification.TenantId, settings.DefaultClinicName, cancellationToken).ConfigureAwait(false);
            var text = MessageComposer.Compose(notification, clinic);
            var plan = await faults.PlanAsync(cancellationToken).ConfigureAwait(false);
            routing = await router.SendAsync(notification, lookup.Contact!, text, clinic.ChannelOrder, round, plan, cancellationToken).ConfigureAwait(false);
        }

        return await RecordAsync(job, notification.Id, routing, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessingOutcome> RecordAsync(ClaimedJob job, Guid notificationId, RoutingResult routing, CancellationToken cancellationToken)
    {
        // Duas tentativas: se o consumidor de cancelamento mexeu no aviso durante o envio (xmin mudou), relê e
        // aplica o resultado de novo. Um envio que aconteceu é registrado como enviado mesmo assim.
        for (var attempt = 0; ; attempt++)
        {
            var notification = await db.Notifications.FirstAsync(n => n.Id == notificationId, cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            db.DeliveryAttempts.AddRange(routing.Attempts);

            ProcessingOutcome outcome;
            DateTimeOffset? reschedule = null;
            if (routing.Delivered)
            {
                Activity.Current?.SetTag("careflow.notification.delivered_via", routing.DeliveredVia!.Value.Slug());
                notification.MarkSent(routing.DeliveredVia!.Value, routing.ProviderMessageId!, now);
                db.Record(notification, NotificationsAudit.SentAction(notification.Kind, routing.DeliveredVia.Value), now);
                outcome = ProcessingOutcome.Sent;
            }
            else if (!notification.IsPending)
            {
                outcome = ProcessingOutcome.Closed;
            }
            else if (notification.RegisterFailedRound() >= options.Value.MaxRounds || notification.AppointmentStartsAt <= now)
            {
                notification.MarkFailed(now);
                db.Record(notification, NotificationsAudit.FailedAction(notification.Kind), now);
                outcome = ProcessingOutcome.Closed;
            }
            else
            {
                reschedule = now + RoundDelay(notification.Rounds, options.Value.RoundRetryDelay, options.Value.MaxRoundRetryDelay);
                outcome = ProcessingOutcome.Rescheduled;
            }

            try
            {
                await SaveWithJobAsync(job, reschedule, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
                continue;
            }

            switch (outcome)
            {
                case ProcessingOutcome.Sent:
                    metrics.Sent(routing.DeliveredVia!.Value, notification.Kind);
                    LogSent(logger, notification.Id, routing.DeliveredVia.Value, routing.Attempts.Count);
                    break;
                case ProcessingOutcome.Rescheduled:
                    LogRoundFailed(logger, notification.Id, notification.Rounds);
                    break;
                default:
                    if (notification.Status == NotificationStatus.Failed)
                    {
                        metrics.ClosedWithoutSending("failed");
                        LogGaveUp(logger, notification.Id, notification.Rounds);
                    }

                    break;
            }

            return outcome;
        }
    }

    private async Task<ProcessingOutcome> NotSentAsync(
        ClaimedJob job, Notification notification, NotSentReason reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        notification.MarkNotSent(reason, now);
        var metricReason = reason == NotSentReason.NoConsent ? "no-consent" : "no-contact";
        return await CloseAsync(job, notification, metricReason, NotificationsAudit.NotSentAction(notification.Kind, reason), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ProcessingOutcome> CloseAsync(
        ClaimedJob job, Notification notification, string metricReason, string? auditAction, CancellationToken cancellationToken)
    {
        if (auditAction is not null)
        {
            db.Record(notification, auditAction, time.GetUtcNow());
        }

        try
        {
            await SaveWithJobAsync(job, reschedule: null, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Outro processo fechou o aviso primeiro (por exemplo, o cancelamento da consulta). A fila é
            // revisitada na próxima reserva, que encontra o aviso fechado.
            db.ChangeTracker.Clear();
            return ProcessingOutcome.Closed;
        }

        metrics.ClosedWithoutSending(metricReason);
        LogClosed(logger, notification.Id, notification.Status);
        return ProcessingOutcome.Closed;
    }

    /// <summary>Grava o aviso e libera a fila (apaga ou reagenda) na mesma transação.</summary>
    private async Task SaveWithJobAsync(ClaimedJob job, DateTimeOffset? reschedule, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await ReleaseAsync(job, reschedule, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Só mexe na fila se a reserva ainda é desta instância (a ficha <c>locked_until</c> bate): se o prazo
    /// venceu e outra instância retomou o aviso, ela é quem decide.
    /// </summary>
    private Task<int> ReleaseAsync(ClaimedJob job, DateTimeOffset? reschedule, CancellationToken cancellationToken)
    {
        var mine = db.Jobs.Where(j => j.NotificationId == job.NotificationId && j.LockedUntil == job.LockedUntil);
        return reschedule is null
            ? mine.ExecuteDeleteAsync(cancellationToken)
            : mine.ExecuteUpdateAsync(
                set => set.SetProperty(j => j.DueAt, reschedule.Value).SetProperty(j => j.LockedUntil, (DateTimeOffset?)null),
                cancellationToken);
    }

    /// <summary>Espera exponencial entre rodadas, com jitter de ±20% para as instâncias não voltarem juntas.</summary>
    internal static TimeSpan RoundDelay(int rounds, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        var raw = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Clamp(rounds - 1, 0, 16));
        var jitter = 0.8 + (RandomNumberGenerator.GetInt32(0, 401) / 1000.0);
        return TimeSpan.FromMilliseconds(Math.Min(raw * jitter, maxDelay.TotalMilliseconds));
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Aviso {NotificationId} enviado por {Channel} ({Attempts} tentativa(s) de canal)")]
    private static partial void LogSent(ILogger logger, Guid notificationId, Domain.Channels.Channel channel, int attempts);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Aviso {NotificationId}: rodada {Round} sem entrega; nova tentativa agendada")]
    private static partial void LogRoundFailed(ILogger logger, Guid notificationId, int round);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error, Message = "Aviso {NotificationId} desistido depois de {Rounds} rodada(s)")]
    private static partial void LogGaveUp(ILogger logger, Guid notificationId, int rounds);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Aviso {NotificationId} encerrado sem envio: {Status}")]
    private static partial void LogClosed(ILogger logger, Guid notificationId, NotificationStatus status);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Warning, Message = "Aviso {NotificationId}: cofre indisponível para ler o contato")]
    private static partial void LogContactUnavailable(ILogger logger, Guid notificationId);
}
