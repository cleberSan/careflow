using CareFlow.Scheduling.Domain.Patients;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Scheduling.Api.Infrastructure.Persistence;

internal sealed class PatientStatusConfiguration : IEntityTypeConfiguration<PatientStatus>
{
    public const string TableName = "patient_statuses";

    public void Configure(EntityTypeBuilder<PatientStatus> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_patient_statuses_consent", "scheduling_consent IN ('Unknown', 'Granted', 'Revoked')");
            table.HasCheckConstraint("ck_patient_statuses_consent_version", "consent_version >= 0");
        });

        builder.HasKey(p => new { p.TenantId, p.PatientRef }).HasName("pk_patient_statuses");
        builder.Property(p => p.TenantId).HasColumnName("tenant_id");
        builder.Property(p => p.PatientRef).HasColumnName("patient_ref");
        builder.Property(p => p.IsRegistered).HasColumnName("is_registered");
        builder.Property(p => p.SchedulingConsent).HasColumnName("scheduling_consent").HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.ConsentVersion).HasColumnName("consent_version");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        builder.Property(p => p.ErasedAt).HasColumnName("erased_at");
        builder.Ignore(p => p.IsErased);
    }
}
