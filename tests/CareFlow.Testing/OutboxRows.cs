using System.Text.Json;

using Npgsql;

namespace CareFlow.Testing;

/// <summary>Evento gravado no Outbox de um serviço, lido como admin nos testes.</summary>
public sealed record OutboxRow(string Type, JsonElement Payload)
{
    /// <summary>Ação do registro de auditoria (<c>audit.data-access</c>), ou <c>null</c> para outros tipos.</summary>
    public string? AuditAction =>
        Type == "audit.data-access" ? Payload.GetProperty("action").GetString() : null;
}

public static class OutboxRows
{
    /// <summary>Eventos da clínica no Outbox, em ordem de criação. Conecta como admin, sem RLS.</summary>
    public static async Task<IReadOnlyList<OutboxRow>> ReadAsync(string adminConnectionString, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT type, payload::text FROM outbox_messages WHERE tenant_id = $1 ORDER BY id",
            connection)
        {
            Parameters = { new() { Value = tenantId } },
        };

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(1));
            rows.Add(new OutboxRow(reader.GetString(0), document.RootElement.Clone()));
        }

        return rows;
    }
}
