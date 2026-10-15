using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RabbitMQ.Client;

namespace CareFlow.BuildingBlocks.Messaging.Broker;

/// <summary>
/// Uma conexão por processo, aberta na primeira necessidade e recuperada automaticamente pelo cliente quando
/// o broker volta. Se a primeira conexão falhar, a próxima chamada tenta de novo.
/// </summary>
public sealed partial class RabbitMqConnection(
    IConfiguration configuration,
    IOptions<MessagingOptions> options,
    ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public bool IsOpen => _connection?.IsOpen == true;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var uri = configuration.GetConnectionString(MessagingOptions.BrokerConnectionStringName);
            if (string.IsNullOrWhiteSpace(uri))
            {
                throw new InvalidOperationException(
                    $"Configure a connection string '{MessagingOptions.BrokerConnectionStringName}' (variável ConnectionStrings__{MessagingOptions.BrokerConnectionStringName}).");
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(uri),
                ClientProvidedName = $"careflow-{options.Value.ServiceName}",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(2),
                RequestedHeartbeat = TimeSpan.FromSeconds(15),
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            LogConnected(logger, options.Value.ServiceName);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    // O endereço do broker pode ter credenciais: nunca vai para o log.
    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Conectado ao RabbitMQ como {ServiceName}")]
    private static partial void LogConnected(ILogger logger, string serviceName);
}
