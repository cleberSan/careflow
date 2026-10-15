using Npgsql;

namespace CareFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// Conexão do despachante, com um login próprio (papel <c>{serviço}_relay</c>). Esse papel lê e atualiza o
/// Outbox de todas as clínicas e apaga Inbox antigo, e não tem acesso a nenhuma tabela de negócio.
/// A API continua sem enxergar eventos de outras clínicas.
/// </summary>
public sealed class RelayDataSource(string connectionString) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = NpgsqlDataSource.Create(connectionString);

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
