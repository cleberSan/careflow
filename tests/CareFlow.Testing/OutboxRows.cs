using System.Text.Json;

using Npgsql;

namespace CareFlow.Testing;

/// <summary>Evento gravado no Outbox de um serviço, lido como admin nos testes.</summary>
public sealed record OutboxRow(string Type, JsonElement Payload)
{
    /// <summary>
    /// Ação do registro de auditoria (<c>audit.data-access</c> ou <c>audit.content-disclosed</c>), ou <c>null</c>
    /// para outros tipos.
    /// </summary>
    public string? AuditAction =>
        Type is "audit.data-access" or "audit.content-disclosed" ? Payload.GetProperty("action").GetString() : null;

    /// <summary>Resumo do conteúdo enviado para fora (<c>audit.content-disclosed</c>), ou <c>null</c>.</summary>
    public string? ContentDigest =>
        Type == "audit.content-disclosed" ? Payload.GetProperty("contentDigest").GetString() : null;
}

public static class OutboxRows
{
    /// <summary>A resposta do serviço a um pedido do titular (<c>rights.subject-request-answered</c>).</summary>
    public static JsonElement AnswerTo(this IReadOnlyList<OutboxRow> rows, Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows
            .Single(row => row.Type == "rights.subject-request-answered" && row.Payload.GetProperty("requestId").GetGuid() == requestId)
            .Payload;
    }

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
