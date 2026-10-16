using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.PatientVault.Api.Infrastructure.Persistence;

internal sealed class PatientKeyRecordConfiguration : IEntityTypeConfiguration<PatientKeyRecord>
{
    public const string TableName = "patient_keys";

    public const string PrimaryKey = "pk_patient_keys";

    public void Configure(EntityTypeBuilder<PatientKeyRecord> builder)
    {
        builder.ToTable(TableName);

        builder.HasKey(k => new { k.TenantId, k.PatientRef }).HasName(PrimaryKey);
        builder.Property(k => k.TenantId).HasColumnName("tenant_id");
        builder.Property(k => k.PatientRef).HasColumnName("patient_ref");
        builder.Property(k => k.TenantKeyVersion).HasColumnName("tenant_key_version");
        builder.Property(k => k.WrappedKey).HasColumnName("wrapped_key");
        builder.Property(k => k.CreatedAt).HasColumnName("created_at");
        builder.Property(k => k.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(k => new { k.TenantId, k.TenantKeyVersion }).HasDatabaseName("ix_patient_keys_tenant_key_version");
    }
}
