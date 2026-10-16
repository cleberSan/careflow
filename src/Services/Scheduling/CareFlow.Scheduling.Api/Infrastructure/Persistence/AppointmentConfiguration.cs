using CareFlow.Scheduling.Domain.Appointments;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Scheduling.Api.Infrastructure.Persistence;

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public const string TableName = "appointments";

    /// <summary>Exclusion constraint (GiST) criada na migration: impede sobreposição de consultas ativas do mesmo profissional.</summary>
    public const string NoOverlapConstraint = "ex_appointments_no_overlap";

    public const string IdempotencyIndex = "ux_appointments_tenant_idempotency_key";

    public void Configure(EntityTypeBuilder<Appointment> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_appointments_interval", "ends_at > starts_at");
            table.HasCheckConstraint("ck_appointments_status", "status IN ('Booked', 'Cancelled')");
            table.HasCheckConstraint("ck_appointments_cancellation_reason", "cancellation_reason IS NULL OR cancellation_reason IN ('Requested', 'ConsentRevoked', 'SubjectErasure')");
        });

        builder.HasKey(a => a.Id).HasName("pk_appointments");
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.TenantId).HasColumnName("tenant_id");
        builder.Property(a => a.ProfessionalId).HasColumnName("professional_id");
        builder.Property(a => a.PatientRef).HasColumnName("patient_ref");
        builder.Property(a => a.StartsAt).HasColumnName("starts_at");
        builder.Property(a => a.EndsAt).HasColumnName("ends_at");
        builder.Property(a => a.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(a => a.CancellationReason).HasColumnName("cancellation_reason").HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(64);
        builder.Property(a => a.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength();

        // Mapeado para a coluna de sistema xmin: concorrência otimista sem coluna extra.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasIndex(a => new { a.TenantId, a.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName(IdempotencyIndex);

        builder.HasIndex(a => new { a.TenantId, a.ProfessionalId, a.StartsAt })
            .HasDatabaseName("ix_appointments_tenant_professional_starts_at");

        // Revogação: consultas futuras e ativas de um paciente.
        builder.HasIndex(a => new { a.TenantId, a.PatientRef, a.StartsAt })
            .HasDatabaseName("ix_appointments_tenant_patient_starts_at")
            .HasFilter("status = 'Booked'");
    }
}
