using System.Diagnostics;

using CareFlow.Contracts;

namespace CareFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// Evento aguardando publicação. É gravado na mesma transação da mudança de negócio, então ou os dois
/// acontecem ou nenhum; o <see cref="MessageRelay"/> publica depois, com retentativas, até o broker confirmar.
/// </summary>
public sealed class OutboxMessage
{
    public const int MaxTypeLength = 128;
    public const int MaxErrorLength = 256;

    private OutboxMessage()
    {
        // Usado pelo EF Core.
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public string? TraceParent { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage Create<TEvent>(Guid tenantId, TEvent @event, DateTimeOffset now)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Todo evento pertence a uma clínica.", nameof(tenantId));
        }

        var occurredAt = ToStoragePrecision(now);
        return new OutboxMessage
        {
            // Versão 7 crescente: ordena por criação (inclusive na mesma transação) e mantém o índice compacto.
            Id = MonotonicGuid.Next(now),
            TenantId = tenantId,
            Type = TEvent.EventType,
            Payload = EventSerializer.Serialize(@event),
            OccurredAt = occurredAt,
            TraceParent = Activity.Current?.Id,
            NextAttemptAt = occurredAt,
        };
    }

    private static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
