namespace CareFlow.BuildingBlocks.Messaging;

/// <summary>Nomes e argumentos do RabbitMQ. Mudar argumentos de uma fila existente exige migração da fila.</summary>
public static class MessagingTopology
{
    /// <summary>Exchange topic onde todos os eventos são publicados; a routing key é o tipo do evento.</summary>
    public const string EventsExchange = "careflow.events";

    /// <summary>Exchange alternativo: evento sem fila ligada vai para cá em vez de ser descartado em silêncio.</summary>
    public const string UnroutedExchange = "careflow.unrouted";

    public const string UnroutedQueue = "careflow.unrouted";

    public const string DeadLetterExchange = "careflow.dead-letter";

    public static string QueueFor(string serviceName) => $"careflow.{serviceName}";

    public static string DeadLetterQueueFor(string serviceName) => $"careflow.{serviceName}.dead-letter";
}

/// <summary>Cabeçalhos do envelope. O corpo é só o JSON do evento.</summary>
public static class MessageHeaders
{
    public const string TenantId = "careflow-tenant-id";
    public const string TraceParent = "traceparent";

    /// <summary>Quando o evento aconteceu, em milissegundos Unix: mede o atraso de propagação até cada consumidor.</summary>
    public const string OccurredAt = "careflow-occurred-at";
    public const string DeliveryCount = "x-delivery-count";
}
