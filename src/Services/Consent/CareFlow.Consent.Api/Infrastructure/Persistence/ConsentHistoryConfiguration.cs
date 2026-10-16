using CareFlow.Consent.Domain.Consents;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Consent.Api.Infrastructure.Persistence;

internal sealed class ConsentHistoryConfiguration : IEntityTypeConfiguration<ConsentHistoryEntry>
{
    public const string TableName = "consent_history";

    public void Configure(EntityTypeBuilder<ConsentHistoryEntry> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_consent_history_change", "change IN ('Granted', 'Revoked')");
            table.HasCheckConstraint("ck_consent_history_version", "version > 0");
        });

        builder.HasKey(h => h.Id).HasName("pk_consent_history");
        builder.Property(h => h.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(h => h.TenantId).HasColumnName("tenant_id");
        builder.Property(h => h.ConsentId).HasColumnName("consent_id");
        builder.Property(h => h.PatientRef).HasColumnName("patient_ref");
        builder.Property(h => h.Purpose).HasColumnName("purpose").HasConversion<string>().HasMaxLength(32);
        builder.Property(h => h.LegalBasis).HasColumnName("legal_basis").HasConversion<string>().HasMaxLength(32);
        builder.Property(h => h.TermVersion).HasColumnName("term_version").HasMaxLength(ConsentRecord.MaxTermVersionLength);
        builder.Property(h => h.Change).HasColumnName("change").HasConversion<string>().HasMaxLength(16);
        builder.Property(h => h.Version).HasColumnName("version");
        builder.Property(h => h.OccurredAt).HasColumnName("occurred_at");

        builder.HasIndex(h => new { h.TenantId, h.PatientRef, h.OccurredAt }).HasDatabaseName("ix_consent_history_patient");
        builder.HasIndex(h => new { h.ConsentId, h.Version }).IsUnique().HasDatabaseName("ux_consent_history_consent_version");
    }
}

/// <summary>Pseudônimo eliminado a pedido do titular: não recebe mais consentimentos (ADR-0012).</summary>
internal sealed class ErasedSubject
{
    public Guid TenantId { get; init; }

    public Guid PatientRef { get; init; }

    public Guid RequestId { get; init; }

    public DateTimeOffset ErasedAt { get; init; }
}

internal sealed class ErasedSubjectConfiguration : IEntityTypeConfiguration<ErasedSubject>
{
    public const string TableName = "erased_subjects";

    public void Configure(EntityTypeBuilder<ErasedSubject> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(e => new { e.TenantId, e.PatientRef }).HasName("pk_erased_subjects");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id");
        builder.Property(e => e.PatientRef).HasColumnName("patient_ref");
        builder.Property(e => e.RequestId).HasColumnName("request_id");
        builder.Property(e => e.ErasedAt).HasColumnName("erased_at");
    }
}
