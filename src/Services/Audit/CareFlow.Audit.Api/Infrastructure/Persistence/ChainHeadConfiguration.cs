using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Audit.Api.Infrastructure.Persistence;

internal sealed class ChainHeadConfiguration : IEntityTypeConfiguration<ChainHeadRecord>
{
    public const string TableName = "audit_chain_heads";

    public void Configure(EntityTypeBuilder<ChainHeadRecord> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_audit_chain_heads_sequence", "sequence >= 0");
            table.HasCheckConstraint("ck_audit_chain_heads_hash_length", "octet_length(hash) = 32");
        });

        builder.HasKey(h => h.TenantId).HasName("pk_audit_chain_heads");
        builder.Property(h => h.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        builder.Property(h => h.Sequence).HasColumnName("sequence");
        builder.Property(h => h.Hash).HasColumnName("hash");
        builder.Property(h => h.UpdatedAt).HasColumnName("updated_at");
    }
}
