using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.PatientVault.Api.Infrastructure.Persistence;

internal sealed class TenantKeyRecordConfiguration : IEntityTypeConfiguration<TenantKeyRecord>
{
    public const string TableName = "tenant_keys";

    public const string PrimaryKey = "pk_tenant_keys";

    public void Configure(EntityTypeBuilder<TenantKeyRecord> builder)
    {
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_tenant_keys_version", "version > 0"));

        builder.HasKey(k => new { k.TenantId, k.Version }).HasName(PrimaryKey);
        builder.Property(k => k.TenantId).HasColumnName("tenant_id");
        builder.Property(k => k.Version).HasColumnName("version");
        builder.Property(k => k.KeyEncryptionKeyId).HasColumnName("kek_id").HasMaxLength(256);
        builder.Property(k => k.WrappedKey).HasColumnName("wrapped_key");
        builder.Property(k => k.CreatedAt).HasColumnName("created_at");
    }
}
