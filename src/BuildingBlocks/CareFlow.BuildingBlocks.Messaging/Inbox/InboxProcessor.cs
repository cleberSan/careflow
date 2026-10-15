using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CareFlow.BuildingBlocks.Messaging.Inbox;

public enum ProcessOutcome
{
    Processed,

    /// <summary>Já processada antes; o efeito não foi aplicado de novo.</summary>
    Duplicate,

    /// <summary>Tipo sem handler ou corpo ilegível. Repetir não adianta: vai para a fila de mensagens mortas.</summary>
    Invalid,
}

/// <summary>
/// Inbox idempotente: numa transação, confere se a mensagem já foi processada por este serviço, aplica o
/// handler e grava o registro do Inbox junto com o efeito. Duas entregas simultâneas da mesma mensagem
/// disputam a chave primária do Inbox e só uma confirma.
/// </summary>
public sealed class InboxProcessor<TContext>(
    IServiceScopeFactory scopes,
    ConsumerRegistry registry,
    TimeProvider time,
    IOptions<MessagingOptions> options)
    where TContext : DbContext
{
    public async Task<ProcessOutcome> ProcessAsync(MessageContext context, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!registry.TryGet(context.EventType, out var dispatcher))
        {
            return ProcessOutcome.Invalid;
        }

        var consumer = options.Value.ServiceName;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(context.TenantId);
            var db = scope.ServiceProvider.GetRequiredService<TContext>();

            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var seen = await db.Set<InboxMessage>()
                    .AnyAsync(m => m.Consumer == consumer && m.MessageId == context.MessageId, cancellationToken)
                    .ConfigureAwait(false);
                if (seen)
                {
                    return ProcessOutcome.Duplicate;
                }

                db.Set<InboxMessage>().Add(new InboxMessage
                {
                    TenantId = context.TenantId,
                    Consumer = consumer,
                    MessageId = context.MessageId,
                    Type = context.EventType,
                    ProcessedAt = time.GetUtcNow(),
                });

                if (!await dispatcher(scope.ServiceProvider, body, context, cancellationToken).ConfigureAwait(false))
                {
                    return ProcessOutcome.Invalid;
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException ex) when (ex.IsUniqueViolation(MessagingModelBuilderExtensions.InboxPrimaryKey))
                {
                    // Outra entrega da mesma mensagem confirmou primeiro; esta é descartada inteira (rollback).
                    return ProcessOutcome.Duplicate;
                }

                return ProcessOutcome.Processed;
            }
        }
    }
}
