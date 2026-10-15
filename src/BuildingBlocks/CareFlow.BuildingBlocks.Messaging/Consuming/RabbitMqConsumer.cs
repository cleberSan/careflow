using System.Diagnostics;
using System.Text;

using CareFlow.BuildingBlocks.Messaging.Broker;
using CareFlow.BuildingBlocks.Messaging.Diagnostics;
using CareFlow.BuildingBlocks.Messaging.Inbox;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CareFlow.BuildingBlocks.Messaging.Consuming;

/// <summary>
/// Consome a fila do serviço com ack manual. Ack só depois do commit do Inbox; falha devolve a mensagem
/// (nack com requeue) após uma espera crescente, e a quorum queue manda para a fila de mensagens mortas
/// depois de <see cref="MessagingOptions.DeliveryLimit"/> entregas. Mensagem inválida vai direto para lá.
/// </summary>
public sealed partial class RabbitMqConsumer<TContext>(
    RabbitMqConnection broker,
    ConsumerRegistry registry,
    InboxProcessor<TContext> processor,
    MessagingDiagnostics diagnostics,
    IOptions<MessagingOptions> options,
    ILogger<RabbitMqConsumer<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartConsumingAsync(settings, stoppingToken).ConfigureAwait(false);
                failures = 0;

                // Daqui em diante o cliente recupera conexão, canal e consumidor sozinho se o broker cair;
                // este laço só mede as filas para as métricas.
                await MeasureQueuesAsync(settings, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogStartFailed(logger, ex.GetType().Name);
                await DisposeChannelAsync().ConfigureAwait(false);
                var delay = Outbox.MessageRelay.Backoff(++failures, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30));
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DisposeChannelAsync().ConfigureAwait(false);
    }

    private async Task StartConsumingAsync(MessagingOptions settings, CancellationToken cancellationToken)
    {
        var connection = await broker.GetAsync(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _channel = channel;

        await TopologyDeclarer.DeclareExchangesAsync(channel, cancellationToken).ConfigureAwait(false);
        await TopologyDeclarer.DeclareConsumerQueueAsync(
            channel, settings.ServiceName, settings.DeliveryLimit, registry.EventTypes, cancellationToken).ConfigureAwait(false);
        await channel.BasicQosAsync(0, settings.Prefetch, global: false, cancellationToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(channel, delivery);

        var queue = MessagingTopology.QueueFor(settings.ServiceName);
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);
        LogConsuming(logger, queue, registry.EventTypes.Count);
    }

    private async Task MeasureQueuesAsync(MessagingOptions settings, CancellationToken stoppingToken)
    {
        string[] queues = [MessagingTopology.QueueFor(settings.ServiceName), MessagingTopology.DeadLetterQueueFor(settings.ServiceName)];
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await broker.GetAsync(stoppingToken).ConfigureAwait(false);

                // Canal próprio e curto: uma declaração passiva que falha fecha o canal, e não pode ser o do consumidor.
                var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                await using (channel.ConfigureAwait(false))
                {
                    foreach (var queue in queues)
                    {
                        var declared = await channel.QueueDeclarePassiveAsync(queue, stoppingToken).ConfigureAwait(false);
                        diagnostics.QueueDepth(queue, declared.MessageCount);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Broker fora: a medida fica com o último valor e volta quando ele voltar.
                LogQueueMeasurementFailed(logger, ex);
            }

            await Task.Delay(settings.MetricsInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs delivery)
    {
        var cancellationToken = delivery.CancellationToken;
        var properties = delivery.BasicProperties;

        if (!TryReadEnvelope(properties, out var context))
        {
            diagnostics.Rejected();
            LogInvalidEnvelope(logger, properties.Type ?? "(sem tipo)");
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        ActivityContext.TryParse(ReadHeader(properties, MessageHeaders.TraceParent), null, out var parent);
        using var activity = MessagingDiagnostics.ActivitySource.StartActivity(
            $"process {context.EventType}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.destination.name", MessagingTopology.QueueFor(options.Value.ServiceName));
        activity?.SetTag("messaging.consumer.group.name", options.Value.ServiceName);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", context.EventType);
        activity?.SetTag("messaging.message.id", context.MessageId.ToString());

        try
        {
            var outcome = await processor.ProcessAsync(context, delivery.Body, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case ProcessOutcome.Processed:
                    diagnostics.Consumed(context.EventType);
                    if (ReadOccurredAt(properties) is { } occurredAt)
                    {
                        diagnostics.DeliveryLag(context.EventType, options.Value.ServiceName, DateTimeOffset.UtcNow - occurredAt);
                    }

                    activity?.SetTag("careflow.inbox.outcome", "processed");
                    break;
                case ProcessOutcome.Duplicate:
                    diagnostics.Duplicate(context.EventType);
                    activity?.SetTag("careflow.inbox.outcome", "duplicate");
                    LogDuplicate(logger, context.EventType, context.MessageId);
                    break;
                default:
                    diagnostics.Rejected();
                    activity?.SetTag("careflow.inbox.outcome", "dead-lettered");
                    activity?.SetStatus(ActivityStatusCode.Error, "invalid-payload");
                    LogInvalidPayload(logger, context.EventType, context.MessageId);
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken).ConfigureAwait(false);
                    return;
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            diagnostics.ConsumeFailed(context.EventType);
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            var deliveryCount = ReadDeliveryCount(properties);
            LogProcessingFailed(logger, context.EventType, context.MessageId, deliveryCount + 1, ex.GetType().Name, ex);

            // Espera antes de devolver, para uma falha passageira (banco reiniciando) não queimar as entregas em milissegundos.
            var delay = TimeSpan.FromMilliseconds(Math.Min(100 * Math.Pow(2, deliveryCount), 5_000));
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool TryReadEnvelope(IReadOnlyBasicProperties properties, out MessageContext context)
    {
        context = null!;
        if (!Guid.TryParse(properties.MessageId, out var messageId)
            || string.IsNullOrWhiteSpace(properties.Type)
            || !Guid.TryParse(ReadHeader(properties, MessageHeaders.TenantId), out var tenantId)
            || tenantId == Guid.Empty)
        {
            return false;
        }

        DateTimeOffset? sentAt = properties.IsTimestampPresent()
            ? DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime)
            : null;
        context = new MessageContext(messageId, tenantId, properties.Type, sentAt);
        return true;
    }

    private static string? ReadHeader(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers is not null && properties.Headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null,
            }
            : null;

    private static DateTimeOffset? ReadOccurredAt(IReadOnlyBasicProperties properties) =>
        properties.Headers is not null && properties.Headers.TryGetValue(MessageHeaders.OccurredAt, out var value)
            ? value switch
            {
                long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms),
                int ms => DateTimeOffset.FromUnixTimeMilliseconds(ms),
                _ => null,
            }
            : null;

    private static long ReadDeliveryCount(IReadOnlyBasicProperties properties) =>
        properties.Headers is not null && properties.Headers.TryGetValue(MessageHeaders.DeliveryCount, out var value)
            ? value switch
            {
                long l => l,
                int i => i,
                _ => 0,
            }
            : 0;

    private async Task DisposeChannelAsync()
    {
        var channel = _channel;
        _channel = null;
        if (channel is not null)
        {
            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogStartFailed(logger, ex.GetType().Name);
            }
        }
    }

    [LoggerMessage(EventId = 9201, Level = LogLevel.Information, Message = "Consumindo a fila {Queue} ({EventTypeCount} tipos de evento)")]
    private static partial void LogConsuming(ILogger logger, string queue, int eventTypeCount);

    [LoggerMessage(EventId = 9202, Level = LogLevel.Warning, Message = "Consumidor não iniciou ({ErrorType}); nova tentativa em seguida")]
    private static partial void LogStartFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 9203, Level = LogLevel.Error, Message = "Mensagem {EventType} sem envelope válido; enviada para mensagens mortas")]
    private static partial void LogInvalidEnvelope(ILogger logger, string eventType);

    [LoggerMessage(EventId = 9204, Level = LogLevel.Error, Message = "Mensagem {EventType} {MessageId} sem handler ou com corpo ilegível; enviada para mensagens mortas")]
    private static partial void LogInvalidPayload(ILogger logger, string eventType, Guid messageId);

    [LoggerMessage(EventId = 9205, Level = LogLevel.Information, Message = "Mensagem {EventType} {MessageId} já processada; ignorada")]
    private static partial void LogDuplicate(ILogger logger, string eventType, Guid messageId);

    // A exceção vai junto para o diagnóstico; handlers não colocam dados pessoais em mensagens de exceção.
    [LoggerMessage(EventId = 9207, Level = LogLevel.Debug, Message = "Medição das filas falhou; nova tentativa no próximo intervalo")]
    private static partial void LogQueueMeasurementFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9206, Level = LogLevel.Warning, Message = "Falha ao processar {EventType} {MessageId} (entrega {Delivery}): {ErrorType}")]
    private static partial void LogProcessingFailed(ILogger logger, string eventType, Guid messageId, long delivery, string errorType, Exception exception);
}
