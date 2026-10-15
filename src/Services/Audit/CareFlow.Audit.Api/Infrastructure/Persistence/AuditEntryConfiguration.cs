using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Audit.Api.Infrastructure.Persistence;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntryRecord>
{
    public const string TableName = "audit_entries";

    public const string MessageIndex = "ux_audit_entries_tenant_message";

    public void Configure(EntityTypeBuilder<AuditEntryRecord> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_audit_entries_sequence", "sequence > 0");
            table.HasCheckConstraint("ck_audit_entries_hash_length", "octet_length(hash) = 32 AND octet_length(previous_hash) = 32");
            table.HasCheckConstraint("ck_audit_entries_content_digest_length", "content_digest IS NULL OR octet_length(content_digest) = 32");
        });

        builder.HasKey(e => new { e.TenantId, e.Sequence }).HasName("pk_audit_entries");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id");
        builder.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedNever();
        builder.Property(e => e.MessageId).HasColumnName("message_id");
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at");
        builder.Property(e => e.RecordedAt).HasColumnName("recorded_at");
        builder.Property(e => e.SourceService).HasColumnName("source_service").HasMaxLength(64);
        builder.Property(e => e.Actor).HasColumnName("actor").HasMaxLength(128);
        builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(64);
        builder.Property(e => e.ResourceType).HasColumnName("resource_type").HasMaxLength(64);
        builder.Property(e => e.ResourceId).HasColumnName("resource_id");
        builder.Property(e => e.ContentDigest).HasColumnName("content_digest");
        builder.Ignore(e => e.ContentDigestHex);
        builder.Property(e => e.KeyId).HasColumnName("key_id").HasMaxLength(64);
        builder.Property(e => e.PreviousHash).HasColumnName("previous_hash");
        builder.Property(e => e.Hash).HasColumnName("hash");

        // Idempotência: o mesmo evento reentregue não vira uma segunda entrada.
        builder.HasIndex(e => new { e.TenantId, e.MessageId }).IsUnique().HasDatabaseName(MessageIndex);

        // Trilha de um recurso ("quem acessou este paciente?") paginada por sequência.
        builder.HasIndex(e => new { e.TenantId, e.ResourceId, e.Sequence }).HasDatabaseName("ix_audit_entries_tenant_resource_sequence");
    }
}
