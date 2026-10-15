using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Notifications.Api.Infrastructure.Persistence;

using Npgsql;

namespace CareFlow.Notifications.Api.Delivery;

/// <summary>Um aviso reservado para esta instância até <see cref="LockedUntil"/> (que também serve de ficha da reserva).</summary>
internal sealed record ClaimedJob(Guid NotificationId, Guid TenantId, DateTimeOffset LockedUntil);

/// <summary>
/// Reserva avisos vencidos de todas as clínicas com o login do despachante (papel <c>notifications_relay</c>),
/// que só enxerga a fila: identificadores, clínica e horários. O processamento de cada aviso roda depois, com o
/// login da API e a clínica do aviso (RLS). <c>FOR UPDATE SKIP LOCKED</c> mais o prazo de reserva garantem que
/// duas instâncias não processam o mesmo aviso ao mesmo tempo, e que um aviso de uma instância que caiu é
/// retomado quando o prazo vence.
/// </summary>
internal sealed class JobQueue(RelayDataSource relay)
{
    private const string ClaimSql =
        $"""
        UPDATE {NotificationJobConfiguration.TableName} AS j
        SET locked_until = @locked_until
        FROM (
            SELECT notification_id
            FROM {NotificationJobConfiguration.TableName}
            WHERE due_at <= @now AND (locked_until IS NULL OR locked_until <= @now)
            ORDER BY due_at
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
        ) AS due
        WHERE j.notification_id = due.notification_id
        RETURNING j.notification_id, j.tenant_id
        """;

    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(DateTimeOffset now, TimeSpan lease, int batch, CancellationToken cancellationToken)
    {
        // Precisão do timestamptz (microssegundos): a ficha comparada depois tem de bater com a gravada.
        var lockedUntil = Truncate(now + lease);

        var connection = await relay.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = new NpgsqlCommand(ClaimSql, connection)
            {
                Parameters =
                {
                    new("locked_until", lockedUntil),
                    new("now", now),
                    new("batch", batch),
                },
            };
            await using (command.ConfigureAwait(false))
            {
                var claimed = new List<ClaimedJob>();
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        claimed.Add(new ClaimedJob(reader.GetGuid(0), reader.GetGuid(1), lockedUntil));
                    }
                }

                return claimed;
            }
        }
    }

    internal static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
