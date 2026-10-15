using CareFlow.Consent.Domain.Consents;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Consent.Api.Infrastructure.Persistence;

internal sealed class ConsentConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public const string TableName = "consents";

    /// <summary>Um registro por paciente e finalidade na clínica.</summary>
    public const string PurposeIndex = "ux_consents_tenant_patient_purpose";

    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_consents_status", "status IN ('Granted', 'Revoked')");
            table.HasCheckConstraint("ck_consents_version", "version > 0");
            table.HasCheckConstraint("ck_consents_revoked_at", "(status = 'Revoked') = (revoked_at IS NOT NULL)");
        });

        builder.HasKey(c => c.Id).HasName("pk_consents");
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.TenantId).HasColumnName("tenant_id");
        builder.Property(c => c.PatientRef).HasColumnName("patient_ref");
        builder.Property(c => c.Purpose).HasColumnName("purpose").HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.LegalBasis).HasColumnName("legal_basis").HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.TermVersion).HasColumnName("term_version").HasMaxLength(ConsentRecord.MaxTermVersionLength);
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.GrantedAt).HasColumnName("granted_at");
        builder.Property(c => c.RevokedAt).HasColumnName("revoked_at");
        builder.Property(c => c.Version).HasColumnName("version");

        // Concorrência otimista pelo xmin: duas revogações/concessões simultâneas não pulam versão.
        builder.Property<uint>("RowVersion").IsRowVersion();

        builder.HasIndex(c => new { c.TenantId, c.PatientRef, c.Purpose })
            .IsUnique()
            .HasDatabaseName(PurposeIndex);
    }
}
