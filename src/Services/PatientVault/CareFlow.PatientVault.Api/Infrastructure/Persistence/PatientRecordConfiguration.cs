using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.PatientVault.Api.Infrastructure.Persistence;

internal sealed class PatientRecordConfiguration : IEntityTypeConfiguration<PatientRecord>
{
    public const string TableName = "patients";

    public const string CpfIndexConstraint = "ux_patients_tenant_cpf_index";

    public void Configure(EntityTypeBuilder<PatientRecord> builder)
    {
        builder.ToTable(TableName, table =>
            table.HasCheckConstraint("ck_patients_cpf_index_length", "octet_length(cpf_index) = 32"));

        builder.HasKey(p => p.PatientRef).HasName("pk_patients");
        builder.Property(p => p.PatientRef).HasColumnName("patient_ref").ValueGeneratedNever();
        builder.Property(p => p.TenantId).HasColumnName("tenant_id");
        builder.Property(p => p.KeyVersion).HasColumnName("key_version");
        builder.Property(p => p.FullName).HasColumnName("full_name_enc");
        builder.Property(p => p.Cpf).HasColumnName("cpf_enc");
        builder.Property(p => p.BirthDate).HasColumnName("birth_date_enc");
        builder.Property(p => p.Email).HasColumnName("email_enc");
        builder.Property(p => p.Phone).HasColumnName("phone_enc");
        builder.Property(p => p.CpfIndex).HasColumnName("cpf_index");
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.HasIndex(p => new { p.TenantId, p.CpfIndex }).IsUnique().HasDatabaseName(CpfIndexConstraint);
    }
}
