using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using CareFlow.BuildingBlocks.Messaging.Broker;
using CareFlow.BuildingBlocks.Messaging.Diagnostics;
using CareFlow.BuildingBlocks.Messaging.Persistence;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using RabbitMQ.Client;

namespace CareFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>O que o despachante faz neste serviço.</summary>
public sealed class MessageRelayOptions
{
    public bool DispatchOutbox { get; set; }

    public bool CleanInbox { get; set; }
}

/// <summary>
/// Publica o Outbox no RabbitMQ e limpa Outbox e Inbox antigos. Cada ciclo trava um lote de pendentes com
/// <c>FOR UPDATE SKIP LOCKED</c> (várias instâncias não publicam a mesma mensagem ao mesmo tempo), publica
/// esperando a confirmação do broker e só então marca como despachada. Se o processo cair entre a confirmação
/// e o commit, a mensagem é publicada de novo: entrega pelo menos uma vez, e o Inbox do consumidor descarta a repetida.
/// </summary>
public sealed partial class MessageRelay(
    RelayDataSource relayDataSource,
    RabbitMqConnection broker,
    OutboxSignal signal,
    MessagingDiagnostics diagnostics,
    TimeProvider time,
    IOptions<MessagingOptions> options,
    IOptions<MessageRelayOptions> relayOptions,
    ILogger<MessageRelay> logger) : BackgroundService
{
    private const string SelectPending =
        $"""
        SELECT id, tenant_id, type, payload, occurred_at, trace_parent, attempts
        FROM {MessagingModelBuilderExtensions.OutboxTable}
        WHERE dispatched_at IS NULL AND next_attempt_at <= @now
        ORDER BY next_attempt_at, id
        LIMIT @batch
        FOR UPDATE SKIP LOCKED
        """;

    private IChannel? _channel;
    private int _consecutiveFailures;
    private DateTimeOffset _nextCleanup;
    private DateTimeOffset _nextBacklogReading;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = settings.RelayPollInterval;
            try
            {
                if (relayOptions.Value.DispatchOutbox)
                {
                    var outcome = await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
                    if (outcome == BatchOutcome.Full)
                    {
                        // Ainda há pendentes: emenda o próximo lote sem esperar.
                        continue;
                    }

                    if (outcome == BatchOutcome.Failed)
                    {
                        delay = Backoff(++_consecutiveFailures, settings.RelayPollInterval, settings.MaxRetryDelay);
                    }
                    else
                    {
                        _consecutiveFailures = 0;
                    }

                    await MeasureBacklogIfDueAsync(stoppingToken).ConfigureAwait(false);
                }

                await CleanupIfDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                // Banco indisponível: nada foi marcado, o próximo ciclo tenta de novo.
                LogCycleFailed(logger, ex.GetType().Name);
                delay = Backoff(++_consecutiveFailures, settings.RelayPollInterval, settings.MaxRetryDelay);
            }

            try
            {
                await signal.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static TimeSpan Backoff(int failures, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        // Exponencial com jitter de ±20%, para instâncias não baterem no broker em sincronia quando ele volta.
        var exponent = Math.Min(failures, 16);
        var raw = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(raw, maxDelay.TotalMilliseconds);
        var jitter = 0.8 + (RandomNumberGenerator.GetInt32(0, 401) / 1000.0);
        return TimeSpan.FromMilliseconds(Math.Min(capped * jitter, maxDelay.TotalMilliseconds));
    }

    private async Task<BatchOutcome> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();

        var connection = await relayDataSource.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var batch = await ReadPendingAsync(connection, transaction, now, settings.RelayBatchSize, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return BatchOutcome.Empty;
                }

                var dispatched = new List<Guid>(batch.Count);
                PendingMessage? failed = null;
                string? failure = null;
                foreach (var message in batch)
                {
                    try
                    {
                        await PublishAsync(message, settings.PublishTimeout, cancellationToken).ConfigureAwait(false);
                        dispatched.Add(message.Id);
                        diagnostics.Published(message.Type);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // Broker fora ou recusou: para o lote para não martelar um broker indisponível.
                        failed = message;
                        failure = ex.GetType().Name;
                        diagnostics.PublishFailed(message.Type);
                        LogPublishFailed(logger, message.Type, message.Id, message.Attempts + 1, failure);
                        await ResetChannelAsync().ConfigureAwait(false);
                        break;
                    }
                }

                var completedAt = time.GetUtcNow();
                await MarkDispatchedAsync(connection, transaction, dispatched, completedAt, cancellationToken).ConfigureAwait(false);
                if (failed is not null)
                {
                    var retryAt = completedAt + Backoff(failed.Attempts + 1, settings.RelayPollInterval, settings.MaxRetryDelay);
                    await MarkFailedAsync(connection, transaction, failed.Id, retryAt, failure!, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                if (dispatched.Count > 0)
                {
                    LogDispatched(logger, dispatched.Count);
                }

                if (failed is not null)
                {
                    return BatchOutcome.Failed;
                }

                return batch.Count == settings.RelayBatchSize ? BatchOutcome.Full : BatchOutcome.Partial;
            }
        }
    }

    private async Task PublishAsync(PendingMessage message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        ActivityContext.TryParse(message.TraceParent, null, out var parent);
        using var activity = MessagingDiagnostics.ActivitySource.StartActivity(
            $"publish {message.Type}", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "send");
        activity?.SetTag("messaging.destination.name", MessagingTopology.EventsExchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", message.Type);
        activity?.SetTag("messaging.message.id", message.Id.ToString());

        var channel = await GetChannelAsync(token).ConfigureAwait(false);
        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            Type = message.Type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                [MessageHeaders.TenantId] = message.TenantId.ToString(),
                [MessageHeaders.TraceParent] = activity?.Id ?? message.TraceParent,
                [MessageHeaders.OccurredAt] = message.OccurredAt.ToUnixTimeMilliseconds(),
            },
        };

        // Com confirmações ligadas, o await só termina quando o broker grava a mensagem (ou lança se recusar).
        await channel.BasicPublishAsync(
            MessagingTopology.EventsExchange,
            message.Type,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.Payload),
            cancellationToken: token).ConfigureAwait(false);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await ResetChannelAsync().ConfigureAwait(false);
        var connection = await broker.GetAsync(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
        await TopologyDeclarer.DeclareExchangesAsync(channel, cancellationToken).ConfigureAwait(false);
        _channel = channel;
        return channel;
    }

    private async Task ResetChannelAsync()
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
                // Canal já quebrado; descartar é tudo o que dá para fazer.
                LogChannelDisposeFailed(logger, ex.GetType().Name);
            }
        }
    }

    /// <summary>Quantos eventos esperam no Outbox e há quanto tempo o mais antigo espera (índice parcial dos pendentes).</summary>
    private async Task MeasureBacklogIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now < _nextBacklogReading)
        {
            return;
        }

        _nextBacklogReading = now + options.Value.MetricsInterval;
        var connection = await relayDataSource.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using var command = new NpgsqlCommand(
                $"SELECT count(*), min(occurred_at) FROM {MessagingModelBuilderExtensions.OutboxTable} WHERE dispatched_at IS NULL",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var pending = reader.GetInt64(0);
            var oldest = reader.IsDBNull(1) ? now : reader.GetFieldValue<DateTimeOffset>(1);
            diagnostics.OutboxBacklog(pending, now - oldest);
        }
    }

    private async Task CleanupIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now + options.Value.CleanupInterval;
        var cutoff = now - options.Value.Retention;

        var connection = await relayDataSource.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var removed = 0;
            if (relayOptions.Value.DispatchOutbox)
            {
                removed += await ExecuteAsync(
                    connection,
                    $"""
                    DELETE FROM {MessagingModelBuilderExtensions.OutboxTable}
                    WHERE id IN (SELECT id FROM {MessagingModelBuilderExtensions.OutboxTable} WHERE dispatched_at < @cutoff LIMIT 5000)
                    """,
                    cutoff,
                    cancellationToken).ConfigureAwait(false);
            }

            if (relayOptions.Value.CleanInbox)
            {
                removed += await ExecuteAsync(
                    connection,
                    $"""
                    DELETE FROM {MessagingModelBuilderExtensions.InboxTable}
                    WHERE ctid IN (SELECT ctid FROM {MessagingModelBuilderExtensions.InboxTable} WHERE processed_at < @cutoff LIMIT 5000)
                    """,
                    cutoff,
                    cancellationToken).ConfigureAwait(false);
            }

            if (removed > 0)
            {
                LogCleaned(logger, removed);
            }
        }
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<PendingMessage>> ReadPendingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectPending, connection, transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch", batchSize);

        var batch = new List<PendingMessage>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            batch.Add(new PendingMessage(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6)));
        }

        return batch;
    }

    private static async Task MarkDispatchedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<Guid> ids,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {MessagingModelBuilderExtensions.OutboxTable}
            SET dispatched_at = @now, attempts = attempts + 1, last_error = NULL
            WHERE id = ANY(@ids)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkFailedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        DateTimeOffset retryAt,
        string error,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {MessagingModelBuilderExtensions.OutboxTable}
            SET attempts = attempts + 1, next_attempt_at = @retry_at, last_error = @error
            WHERE id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("retry_at", retryAt);
        command.Parameters.AddWithValue("error", error.Length > OutboxMessage.MaxErrorLength ? error[..OutboxMessage.MaxErrorLength] : error);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private enum BatchOutcome
    {
        Empty,
        Partial,
        Full,
        Failed,
    }

    private sealed record PendingMessage(
        Guid Id,
        Guid TenantId,
        string Type,
        string Payload,
        DateTimeOffset OccurredAt,
        string? TraceParent,
        int Attempts);

    // Só o tipo da exceção: a mensagem de erro do cliente pode trazer o endereço do broker.
    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning, Message = "Outbox: falha ao publicar {EventType} {MessageId} (tentativa {Attempt}): {ErrorType}")]
    private static partial void LogPublishFailed(ILogger logger, string eventType, Guid messageId, int attempt, string errorType);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Debug, Message = "Outbox: {Count} mensagens publicadas")]
    private static partial void LogDispatched(ILogger logger, int count);

    [LoggerMessage(EventId = 9103, Level = LogLevel.Warning, Message = "Outbox: ciclo do despachante falhou ({ErrorType}); nova tentativa em seguida")]
    private static partial void LogCycleFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 9104, Level = LogLevel.Information, Message = "Mensageria: {Count} registros antigos de Outbox/Inbox removidos")]
    private static partial void LogCleaned(ILogger logger, int count);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Warning, Message = "Mensageria: canal descartado com erro {ErrorType}")]
    private static partial void LogChannelDisposeFailed(ILogger logger, string errorType);
}
