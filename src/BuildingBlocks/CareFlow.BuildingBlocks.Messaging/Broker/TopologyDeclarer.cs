using RabbitMQ.Client;

namespace CareFlow.BuildingBlocks.Messaging.Broker;

/// <summary>
/// Declara exchanges e filas. Declarações são idempotentes, então publicador e consumidor declaram o que usam
/// e a ordem de subida dos serviços não importa.
/// </summary>
internal static class TopologyDeclarer
{
    public static async Task DeclareExchangesAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            MessagingTopology.UnroutedExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueDeclareAsync(
            MessagingTopology.UnroutedQueue, durable: true, exclusive: false, autoDelete: false, arguments: QuorumArguments(), cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueBindAsync(
            MessagingTopology.UnroutedQueue, MessagingTopology.UnroutedExchange, string.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            MessagingTopology.EventsExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["alternate-exchange"] = MessagingTopology.UnroutedExchange },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            MessagingTopology.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fila quorum do serviço, ligada aos tipos de evento que ele trata, com fila de mensagens mortas.</summary>
    public static async Task DeclareConsumerQueueAsync(
        IChannel channel,
        string serviceName,
        int deliveryLimit,
        IEnumerable<string> eventTypes,
        CancellationToken cancellationToken)
    {
        var queue = MessagingTopology.QueueFor(serviceName);
        var deadLetterQueue = MessagingTopology.DeadLetterQueueFor(serviceName);

        await channel.QueueDeclareAsync(
            deadLetterQueue, durable: true, exclusive: false, autoDelete: false, arguments: QuorumArguments(), cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueBindAsync(deadLetterQueue, MessagingTopology.DeadLetterExchange, queue, cancellationToken: cancellationToken).ConfigureAwait(false);

        var arguments = QuorumArguments();
        arguments["x-delivery-limit"] = deliveryLimit;
        arguments["x-dead-letter-exchange"] = MessagingTopology.DeadLetterExchange;
        arguments["x-dead-letter-routing-key"] = queue;
        await channel.QueueDeclareAsync(
            queue, durable: true, exclusive: false, autoDelete: false, arguments: arguments, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var eventType in eventTypes)
        {
            await channel.QueueBindAsync(queue, MessagingTopology.EventsExchange, eventType, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    // Quorum queues: replicadas e duráveis, recomendadas no RabbitMQ 4 no lugar das filas clássicas espelhadas.
    private static Dictionary<string, object?> QuorumArguments() => new() { ["x-queue-type"] = "quorum" };
}
