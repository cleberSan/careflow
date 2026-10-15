using CareFlow.Notifications.Domain.Appointments;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Clinics;
using CareFlow.Notifications.Domain.Consents;
using CareFlow.Notifications.Domain.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Notifications.Api.Infrastructure.Persistence;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public const string TableName = "notifications";
    public const string OnePerKindIndex = "ux_notifications_tenant_appointment_kind";

    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_notifications_kind", "kind IN ('Confirmation', 'Reminder', 'Cancellation')");
            table.HasCheckConstraint("ck_notifications_status", "status IN ('Pending', 'Sent', 'NotSent', 'Superseded', 'Expired', 'Failed')");
            table.HasCheckConstraint("ck_notifications_rounds", "rounds >= 0");
            table.HasCheckConstraint("ck_notifications_sent", "status <> 'Sent' OR (sent_via IS NOT NULL AND provider_message_id IS NOT NULL)");
            table.HasCheckConstraint("ck_notifications_not_sent", "status <> 'NotSent' OR not_sent_reason IS NOT NULL");
        });

        builder.HasKey(n => n.Id).HasName("pk_notifications");
        builder.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(n => n.TenantId).HasColumnName("tenant_id");
        builder.Property(n => n.AppointmentId).HasColumnName("appointment_id");
        builder.Property(n => n.PatientRef).HasColumnName("patient_ref");
        builder.Property(n => n.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.AppointmentStartsAt).HasColumnName("appointment_starts_at");
        builder.Property(n => n.CancellationCause).HasColumnName("cancellation_cause").HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.DueAt).HasColumnName("due_at");
        builder.Property(n => n.Rounds).HasColumnName("rounds");
        builder.Property(n => n.SentVia).HasColumnName("sent_via").HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(128);
        builder.Property(n => n.NotSentReason).HasColumnName("not_sent_reason").HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.CreatedAt).HasColumnName("created_at");
        builder.Property(n => n.CompletedAt).HasColumnName("completed_at");

        // Coluna de sistema xmin: o despachante e o consumidor de cancelamento não sobrescrevem um ao outro.
        builder.Property(n => n.Version).IsRowVersion();
        builder.Ignore(n => n.IsPending);

        // Um aviso de cada tipo por consulta, mesmo que o evento chegue de novo com outro identificador.
        builder.HasIndex(n => new { n.TenantId, n.AppointmentId, n.Kind }).IsUnique().HasDatabaseName(OnePerKindIndex);
    }
}

internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public const string TableName = "delivery_attempts";

    public void Configure(EntityTypeBuilder<DeliveryAttempt> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_delivery_attempts_channel", "channel IN ('WhatsApp', 'Sms', 'Email')");
            table.HasCheckConstraint("ck_delivery_attempts_outcome", "outcome IN ('Delivered', 'Failed', 'CircuitOpen', 'NoContact')");
        });

        builder.HasKey(a => a.Id).HasName("pk_delivery_attempts");
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.TenantId).HasColumnName("tenant_id");
        builder.Property(a => a.NotificationId).HasColumnName("notification_id");
        builder.Property(a => a.Round).HasColumnName("round");
        builder.Property(a => a.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(64);
        builder.Property(a => a.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.Error).HasColumnName("error").HasMaxLength(DeliveryAttempt.MaxErrorLength);
        builder.Property(a => a.AttemptedAt).HasColumnName("attempted_at");

        builder.HasOne<Notification>().WithMany().HasForeignKey(a => a.NotificationId)
            .HasConstraintName("fk_delivery_attempts_notifications").OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => a.NotificationId).HasDatabaseName("ix_delivery_attempts_notification_id");
    }
}

internal sealed class NotificationJobConfiguration : IEntityTypeConfiguration<NotificationJob>
{
    public const string TableName = "notification_jobs";

    public void Configure(EntityTypeBuilder<NotificationJob> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(j => j.NotificationId).HasName("pk_notification_jobs");
        builder.Property(j => j.NotificationId).HasColumnName("notification_id").ValueGeneratedNever();
        builder.Property(j => j.TenantId).HasColumnName("tenant_id");
        builder.Property(j => j.DueAt).HasColumnName("due_at");
        builder.Property(j => j.LockedUntil).HasColumnName("locked_until");

        builder.HasOne<Notification>().WithOne().HasForeignKey<NotificationJob>(j => j.NotificationId)
            .HasConstraintName("fk_notification_jobs_notifications").OnDelete(DeleteBehavior.Cascade);

        // O despachante busca por horário; a fila só tem pendentes, então o índice não cresce com o histórico.
        builder.HasIndex(j => j.DueAt).HasDatabaseName("ix_notification_jobs_due_at");
    }
}

internal sealed class AppointmentTrackConfiguration : IEntityTypeConfiguration<AppointmentTrack>
{
    public const string TableName = "appointment_tracks";

    public void Configure(EntityTypeBuilder<AppointmentTrack> builder)
    {
        builder.ToTable(TableName, table =>
            table.HasCheckConstraint("ck_appointment_tracks_state", "state IN ('Unknown', 'Booked', 'Cancelled')"));
        builder.HasKey(a => new { a.TenantId, a.AppointmentId }).HasName("pk_appointment_tracks");
        builder.Property(a => a.TenantId).HasColumnName("tenant_id");
        builder.Property(a => a.AppointmentId).HasColumnName("appointment_id");
        builder.Property(a => a.PatientRef).HasColumnName("patient_ref");
        builder.Property(a => a.StartsAt).HasColumnName("starts_at");
        builder.Property(a => a.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");
    }
}

internal sealed class NotificationConsentConfiguration : IEntityTypeConfiguration<NotificationConsent>
{
    public const string TableName = "notification_consents";

    public void Configure(EntityTypeBuilder<NotificationConsent> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_notification_consents_state", "state IN ('Unknown', 'Granted', 'Revoked')");
            table.HasCheckConstraint("ck_notification_consents_version", "version >= 0");
        });
        builder.HasKey(c => new { c.TenantId, c.PatientRef }).HasName("pk_notification_consents");
        builder.Property(c => c.TenantId).HasColumnName("tenant_id");
        builder.Property(c => c.PatientRef).HasColumnName("patient_ref");
        builder.Property(c => c.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.Version).HasColumnName("version");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");
    }
}

internal sealed class ClinicSettingsConfiguration : IEntityTypeConfiguration<ClinicSettings>
{
    public const string TableName = "clinic_settings";

    public void Configure(EntityTypeBuilder<ClinicSettings> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(s => s.TenantId).HasName("pk_clinic_settings");
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        builder.Property(s => s.DisplayName).HasColumnName("display_name").HasMaxLength(ClinicSettings.MaxDisplayNameLength);
        builder.Property(s => s.TimeZoneId).HasColumnName("time_zone").HasMaxLength(64);
        builder.Property(s => s.ChannelOrder).HasColumnName("channel_order").HasMaxLength(64)
            .HasConversion(order => order.ToString(), stored => ChannelOrder.Parse(stored));
        builder.Property(s => s.ReminderLead).HasColumnName("reminder_lead");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        builder.Property<uint>("Version").IsRowVersion();
    }
}
