using CareFlow.Audit.Api.Chain;
using CareFlow.Audit.Api.Common.Observability;
using CareFlow.Audit.Api.Infrastructure.Persistence;
using CareFlow.Audit.Domain.Chain;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CareFlow.Audit.Api.Features.Entries;

internal enum AppendOutcome
{
    Appended,
    Duplicate,
}

/// <summary>
/// Acrescenta um evento à cadeia da clínica. Numa transação: garante a linha da cabeça, trava a cabeça,
/// ignora a mensagem se já foi registrada, calcula o próximo hash e grava entrada e nova cabeça juntas.
/// </summary>
internal sealed partial class AuditWriter(
    AuditDbContext db,
    ITenantContext tenant,
    ChainKeyRing keys,
    TimeProvider time,
    AuditMetrics metrics,
    ILogger<AuditWriter> logger)
{
    public async Task<AppendOutcome> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // O banco só aceita linhas da clínica da sessão (RLS); conferir aqui dá um erro claro em vez de uma violação de política.
        if (auditEvent.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException("O evento de auditoria pertence a outra clínica que não a do contexto atual.");
        }

        var tenantId = auditEvent.TenantId;
        var now = time.GetUtcNow();
        var genesis = ChainHead.Genesis.Hash;

        // No consumidor, o Inbox já abriu a transação (o registro do Inbox e a entrada são gravados juntos).
        var ownTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO audit_chain_heads (tenant_id, sequence, hash, updated_at)
                VALUES ({tenantId}, 0, {genesis}, {now})
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                cancellationToken).ConfigureAwait(false);

            var head = await db.ChainHeads
                .FromSql($"SELECT * FROM audit_chain_heads WHERE tenant_id = {tenantId} FOR UPDATE")
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);

            // Com a cabeça travada, nenhuma outra gravação desta clínica corre em paralelo: a checagem é segura.
            var alreadyRecorded = await db.Entries
                .AnyAsync(e => e.MessageId == auditEvent.MessageId, cancellationToken)
                .ConfigureAwait(false);
            if (alreadyRecorded)
            {
                await CommitAsync(ownTransaction, cancellationToken).ConfigureAwait(false);
                metrics.Duplicate();
                LogDuplicate(logger, auditEvent.Action);
                return AppendOutcome.Duplicate;
            }

            var entry = AuditChain.Append(new ChainHead(head.Sequence, head.Hash), auditEvent, now, keys.Active);
            db.Entries.Add(AuditEntryRecord.From(entry));
            head.Sequence = entry.Sequence;
            head.Hash = entry.Hash;
            head.UpdatedAt = entry.RecordedAt;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await CommitAsync(ownTransaction, cancellationToken).ConfigureAwait(false);

            metrics.Appended(auditEvent.Action);
            LogAppended(logger, entry.Sequence, auditEvent.Action, auditEvent.SourceService);
            return AppendOutcome.Appended;
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken) =>
        transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    // Sem ator, pseudônimo ou clínica nos logs: a trilha completa mora só na tabela de auditoria.
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Auditoria: entrada {Sequence} gravada ({Action} vindo de {SourceService})")]
    private static partial void LogAppended(ILogger logger, long sequence, string action, string sourceService);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Auditoria: evento {Action} já registrado, ignorado")]
    private static partial void LogDuplicate(ILogger logger, string action);
}
