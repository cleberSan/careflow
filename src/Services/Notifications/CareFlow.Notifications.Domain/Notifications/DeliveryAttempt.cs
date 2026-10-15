using CareFlow.Notifications.Domain.Channels;

namespace CareFlow.Notifications.Domain.Notifications;

public enum AttemptOutcome
{
    /// <summary>O provedor aceitou a mensagem.</summary>
    Delivered = 1,

    /// <summary>O provedor falhou depois das retentativas (erro, timeout ou indisponibilidade).</summary>
    Failed,

    /// <summary>O circuit breaker do canal estava aberto: nem tentou, passou para o próximo canal.</summary>
    CircuitOpen,

    /// <summary>O paciente não tem o contato que o canal usa (telefone ou e-mail).</summary>
    NoContact,
}

/// <summary>
/// Uma tentativa por um canal, para a trilha do envio e o Painel de resiliência. Sem contato nem texto: só
/// canal, chave de idempotência e resultado.
/// </summary>
public sealed class DeliveryAttempt
{
    public const int MaxErrorLength = 64;

    private DeliveryAttempt()
    {
        // Usado pelo EF Core.
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid NotificationId { get; private set; }

    public int Round { get; private set; }

    public Channel Channel { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public AttemptOutcome Outcome { get; private set; }

    /// <summary>Categoria técnica do erro (nome do tipo da exceção ou status HTTP), nunca conteúdo da resposta.</summary>
    public string? Error { get; private set; }

    public DateTimeOffset AttemptedAt { get; private set; }

    public static DeliveryAttempt Record(Notification notification, int round, Channel channel, AttemptOutcome outcome, string? error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return new DeliveryAttempt
        {
            Id = Guid.CreateVersion7(now),
            TenantId = notification.TenantId,
            NotificationId = notification.Id,
            Round = round,
            Channel = channel,
            IdempotencyKey = notification.IdempotencyKeyFor(channel),
            Outcome = outcome,
            Error = error is null ? null : error[..Math.Min(error.Length, MaxErrorLength)],
            AttemptedAt = now,
        };
    }
}
