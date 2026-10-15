using System.ComponentModel.DataAnnotations;

namespace CareFlow.BuildingBlocks.Messaging;

/// <summary>Configuração da mensageria (seção <c>Messaging</c>). O endereço do broker fica em <c>ConnectionStrings:RabbitMq</c>.</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public const string BrokerConnectionStringName = "RabbitMq";

    /// <summary>
    /// Liga o despachante do Outbox e os consumidores. Desligado, os eventos continuam gravados no Outbox
    /// (nada se perde) e saem quando a mensageria for ligada. Usado nos testes que não precisam de broker.
    /// </summary>
    public bool Enabled { get; set; } = true;

    [Required]
    [RegularExpression("^[a-z][a-z0-9-]{1,40}$")]
    public string ServiceName { get; set; } = string.Empty;

    [Range(1, 1000)]
    public ushort Prefetch { get; set; } = 16;

    /// <summary>Entregas antes de a mensagem ir para a fila de mensagens mortas (quorum queue <c>x-delivery-limit</c>).</summary>
    [Range(1, 100)]
    public int DeliveryLimit { get; set; } = 10;

    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan RelayPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    [Range(1, 1000)]
    public int RelayBatchSize { get; set; } = 50;

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);

    [Range(typeof(TimeSpan), "00:00:00.010", "01:00:00")]
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Por quanto tempo mensagens já despachadas (Outbox) e já processadas (Inbox) ficam guardadas.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "90.00:00:00")]
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
}
