using System.Linq.Expressions;

using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.BuildingBlocks.Messaging.Outbox;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.BuildingBlocks.Messaging.Persistence;

public static class MessagingModelBuilderExtensions
{
    public const string OutboxTable = "outbox_messages";
    public const string InboxTable = "inbox_messages";
    public const string InboxPrimaryKey = "pk_inbox_messages";

    /// <summary>
    /// Mapeia a tabela do Outbox, filtrada pela clínica como as tabelas de negócio. Passe uma propriedade do
    /// próprio DbContext (<c>() =&gt; CurrentTenantId</c>): o EF só reavalia por instância expressões que leem
    /// membros do contexto; um valor capturado de fora ficaria fixo no modelo, que é cacheado.
    /// </summary>
    public static ModelBuilder AddOutbox(this ModelBuilder modelBuilder, Expression<Func<Guid>> currentTenantId)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(currentTenantId);

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable(OutboxTable, table => table.HasCheckConstraint("ck_outbox_messages_attempts", "attempts >= 0"));
            builder.HasKey(m => m.Id).HasName("pk_outbox_messages");
            builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(m => m.TenantId).HasColumnName("tenant_id");
            builder.Property(m => m.Type).HasColumnName("type").HasMaxLength(OutboxMessage.MaxTypeLength);
            builder.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
            builder.Property(m => m.OccurredAt).HasColumnName("occurred_at");
            builder.Property(m => m.TraceParent).HasColumnName("trace_parent").HasMaxLength(64);
            builder.Property(m => m.Attempts).HasColumnName("attempts");
            builder.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at");
            builder.Property(m => m.DispatchedAt).HasColumnName("dispatched_at");
            builder.Property(m => m.LastError).HasColumnName("last_error").HasMaxLength(OutboxMessage.MaxErrorLength);

            // Índice parcial: o despachante só olha pendentes, então o índice não cresce com o histórico.
            builder.HasIndex(m => new { m.NextAttemptAt, m.Id })
                .HasDatabaseName("ix_outbox_messages_pending")
                .HasFilter("dispatched_at IS NULL");
            builder.HasIndex(m => m.DispatchedAt)
                .HasDatabaseName("ix_outbox_messages_dispatched_at")
                .HasFilter("dispatched_at IS NOT NULL");

            builder.HasQueryFilter(TenantFilter<OutboxMessage>(m => m.TenantId, currentTenantId));
        });

        return modelBuilder;
    }

    /// <summary>Mapeia a tabela do Inbox, filtrada pela clínica da mensagem em processamento (veja <see cref="AddOutbox"/>).</summary>
    public static ModelBuilder AddInbox(this ModelBuilder modelBuilder, Expression<Func<Guid>> currentTenantId)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(currentTenantId);

        modelBuilder.Entity<InboxMessage>(builder =>
        {
            builder.ToTable(InboxTable);
            builder.HasKey(m => new { m.Consumer, m.MessageId }).HasName(InboxPrimaryKey);
            builder.Property(m => m.TenantId).HasColumnName("tenant_id");
            builder.Property(m => m.Consumer).HasColumnName("consumer").HasMaxLength(InboxMessage.MaxConsumerLength);
            builder.Property(m => m.MessageId).HasColumnName("message_id");
            builder.Property(m => m.Type).HasColumnName("type").HasMaxLength(OutboxMessage.MaxTypeLength);
            builder.Property(m => m.ProcessedAt).HasColumnName("processed_at");
            builder.HasIndex(m => m.ProcessedAt).HasDatabaseName("ix_inbox_messages_processed_at");

            builder.HasQueryFilter(TenantFilter<InboxMessage>(m => m.TenantId, currentTenantId));
        });

        return modelBuilder;
    }

    private static Expression<Func<TEntity, bool>> TenantFilter<TEntity>(
        Expression<Func<TEntity, Guid>> tenantColumn,
        Expression<Func<Guid>> currentTenantId) =>
        Expression.Lambda<Func<TEntity, bool>>(
            Expression.Equal(tenantColumn.Body, currentTenantId.Body),
            tenantColumn.Parameters);
}
