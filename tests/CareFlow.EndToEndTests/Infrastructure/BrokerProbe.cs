using System.Text;
using System.Text.Json;

using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Contracts;

using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

using Testcontainers.RabbitMq;

namespace CareFlow.EndToEndTests.Infrastructure;

/// <summary>
/// Acesso direto ao RabbitMQ nos testes: publicar mensagens forjadas (duplicadas, fora de ordem, inválidas),
/// contar mensagens numa fila e parar ou religar o broker sem trocar a porta do container.
/// </summary>
public sealed class BrokerProbe : IAsyncDisposable
{
    private readonly RabbitMqContainer _container;
    private readonly string _uri;
    private IConnection? _connection;

    private BrokerProbe(RabbitMqContainer container, string uri)
    {
        _container = container;
        _uri = uri;
    }

    public static Task<BrokerProbe> ConnectAsync(RabbitMqContainer container, string uri) =>
        Task.FromResult(new BrokerProbe(container, uri));

    /// <summary>Publica um evento como o despachante publicaria, com o envelope informado.</summary>
    public Task PublishAsync<TEvent>(Guid tenantId, TEvent @event, Guid messageId)
        where TEvent : IIntegrationEvent =>
        PublishRawAsync(TEvent.EventType, tenantId.ToString(), JsonSerializer.SerializeToUtf8Bytes(@event, JsonSerializerOptions.Web), messageId);

    public async Task PublishRawAsync(string eventType, string? tenantHeader, byte[] body, Guid messageId)
    {
        await using var channel = await (await ConnectionAsync()).CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            TestContext.Current.CancellationToken);
        var headers = new Dictionary<string, object?>();
        if (tenantHeader is not null)
        {
            headers[MessageHeaders.TenantId] = tenantHeader;
        }

        var properties = new BasicProperties
        {
            MessageId = messageId.ToString(),
            Type = eventType,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers,
        };
        await channel.BasicPublishAsync(
            MessagingTopology.EventsExchange, eventType, mandatory: true, properties, body, TestContext.Current.CancellationToken);
    }

    /// <summary>Quantidade de mensagens prontas na fila.</summary>
    public async Task<uint> CountAsync(string queue)
    {
        await using var channel = await (await ConnectionAsync()).CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        var declared = await channel.QueueDeclarePassiveAsync(queue, TestContext.Current.CancellationToken);
        return declared.MessageCount;
    }

    /// <summary>Lê e confirma a próxima mensagem da fila, ou <c>null</c> se estiver vazia.</summary>
    public async Task<BasicGetResult?> TakeAsync(string queue)
    {
        await using var channel = await (await ConnectionAsync()).CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        return await channel.BasicGetAsync(queue, autoAck: true, TestContext.Current.CancellationToken);
    }

    public async Task WaitForQueuesAsync(params string[] queues)
    {
        foreach (var queue in queues)
        {
            await Eventually.SucceedsAsync(async () =>
            {
                try
                {
                    await CountAsync(queue);
                    return true;
                }
                catch (OperationInterruptedException)
                {
                    return false;
                }
            });
        }
    }

    /// <summary>Para a aplicação RabbitMQ dentro do container: conexões caem, a porta continua a mesma.</summary>
    public async Task StopAsync()
    {
        await DropConnectionAsync();
        var result = await _container.ExecAsync(["rabbitmqctl", "stop_app"], TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
    }

    public async Task StartAsync()
    {
        var result = await _container.ExecAsync(["rabbitmqctl", "start_app"], TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        await _container.ExecAsync(["rabbitmqctl", "await_startup"], TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await DropConnectionAsync();

    private async Task<IConnection> ConnectionAsync()
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await DropConnectionAsync();
        var factory = new ConnectionFactory { Uri = new Uri(_uri), ClientProvidedName = "careflow-e2e-probe" };
        _connection = await factory.CreateConnectionAsync(TestContext.Current.CancellationToken);
        return _connection;
    }

    private async Task DropConnectionAsync()
    {
        if (_connection is not null)
        {
            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Conexão já derrubada pelo broker.
            }

            _connection = null;
        }
    }
}
