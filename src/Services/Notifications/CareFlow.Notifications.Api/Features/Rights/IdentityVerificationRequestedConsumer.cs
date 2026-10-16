using System.Globalization;

using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Contracts.Rights;
using CareFlow.Notifications.Api.Channels;
using CareFlow.Notifications.Api.Common;
using CareFlow.Notifications.Api.Common.Observability;
using CareFlow.Notifications.Api.Contacts;
using CareFlow.Notifications.Api.Infrastructure;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Channels;

using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Features.Rights;

/// <summary>
/// Envia o código de confirmação de um pedido do titular para o contato cadastrado no cofre, na ordem de canais da
/// clínica (ADR-0012). O envio acontece dentro da transação do Inbox: se a gravação falhar depois de o provedor
/// aceitar, a mensagem volta e o mesmo código sai com a mesma chave de idempotência (o provedor deduplica).
/// O texto nunca vai para log, evento nem banco; o código chega aqui cifrado e só existe em claro na memória.
/// </summary>
internal sealed partial class IdentityVerificationRequestedConsumer(
    NotificationsDbContext db,
    VerificationCodeKey key,
    IContactDirectory contacts,
    ChannelGateway gateway,
    IOptions<NotificationOptions> settings,
    NotificationsMetrics metrics,
    TimeProvider time,
    ILogger<IdentityVerificationRequestedConsumer> logger)
    : IIntegrationEventConsumer<IdentityVerificationRequested>
{
    public const string SentAction = "identity-verification.sent";
    public const string NotSentAction = "identity-verification.not-sent";

    /// <summary>Prefixo da chave de idempotência: por clínica e pedido, para a caixa simulada achar a mensagem.</summary>
    public static string KeyPrefix(Guid tenantId, Guid requestId) => $"verify-{tenantId:N}-{requestId:N}-";

    public async Task HandleAsync(IdentityVerificationRequested @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        var now = time.GetUtcNow();
        if (@event.ExpiresAt <= now)
        {
            // Código vencido antes de sair: não adianta mandar.
            Answer(context.TenantId, @event, IdentityVerificationDispatched.Failed, null, now);
            return;
        }

        var code = key.TryOpen(@event.RequestId, @event.SealedCode)
            ?? throw new PoisonMessageException("Notificacoes.CodigoDeVerificacaoInvalido");

        var lookup = await contacts.FindAsync(context.TenantId, @event.PatientRef, cancellationToken).ConfigureAwait(false);
        if (lookup.Status == ContactLookupStatus.Unavailable)
        {
            // O cofre não respondeu: a mensagem volta para a fila e tenta de novo.
            throw new InvalidOperationException("Cofre indisponível para buscar o contato.");
        }

        if (lookup.Status == ContactLookupStatus.NotFound || lookup.Contact is null || lookup.Contact.IsEmpty)
        {
            Answer(context.TenantId, @event, IdentityVerificationDispatched.NoContact, null, now);
            return;
        }

        var clinic = await ProjectionStore.ClinicSettingsAsync(db, context.TenantId, settings.Value.DefaultClinicName, cancellationToken).ConfigureAwait(false);
        var text = Text(clinic.DisplayName, code, @event.ExpiresAt - now);
        foreach (var channel in clinic.ChannelOrder.Channels)
        {
            var address = lookup.Contact.AddressFor(channel);
            if (address is null)
            {
                continue;
            }

            var result = await gateway.SendAsync(
                channel, $"{KeyPrefix(context.TenantId, @event.RequestId)}{context.MessageId:N}-{channel.Slug()}", address, text, cancellationToken)
                .ConfigureAwait(false);
            if (result.Kind == ChannelResultKind.Delivered)
            {
                Answer(context.TenantId, @event, IdentityVerificationDispatched.Sent, channel, now);
                return;
            }

            metrics.ChannelFailed(channel, result.Kind == ChannelResultKind.CircuitOpen ? Domain.Notifications.AttemptOutcome.CircuitOpen : Domain.Notifications.AttemptOutcome.Failed);
        }

        LogNotSent(logger);
        Answer(context.TenantId, @event, IdentityVerificationDispatched.Failed, null, now);
    }

    private void Answer(Guid tenantId, IdentityVerificationRequested request, string outcome, Channel? channel, DateTimeOffset now)
    {
        db.Enqueue(tenantId, new IdentityVerificationDispatched(request.RequestId, outcome, channel?.Slug(), now), now);
        db.RecordAccess(
            tenantId,
            NotificationsAudit.SourceService,
            NotificationsAudit.SystemActor,
            outcome == IdentityVerificationDispatched.Sent ? $"{SentAction}.{channel!.Value.Slug()}" : $"{NotSentAction}.{outcome}",
            NotificationsAudit.ResourceType,
            request.PatientRef,
            now);
    }

    internal static string Text(string clinicName, string code, TimeSpan validity) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{clinicName}: seu código para confirmar o pedido sobre seus dados é {code}. Ele vale por {Math.Max(1, (int)Math.Floor(validity.TotalMinutes))} minutos. Se você não fez este pedido, ignore esta mensagem.");

    [LoggerMessage(EventId = 3301, Level = LogLevel.Warning, Message = "Código de confirmação não saiu por nenhum canal")]
    private static partial void LogNotSent(ILogger logger);
}
