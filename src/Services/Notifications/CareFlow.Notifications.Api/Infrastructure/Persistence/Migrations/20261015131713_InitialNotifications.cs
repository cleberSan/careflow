using System;
using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Notifications.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 5: avisos, tentativas, fila de envios, projeções (consulta e consentimento de notificações),
    /// configuração por clínica, Outbox e Inbox. Todas as tabelas com RLS por clínica. O despachante
    /// (notifications_relay) só enxerga Outbox, Inbox e a fila, que tem apenas identificadores e horários.
    /// </summary>
    public partial class InitialNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "appointment_tracks",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_appointment_tracks", x => new { x.tenant_id, x.appointment_id });
                    table.CheckConstraint("ck_appointment_tracks_state", "state IN ('Unknown', 'Booked', 'Cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "clinic_settings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel_order = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reminder_lead = table.Column<TimeSpan>(type: "interval", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clinic_settings", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateTable(
                name: "notification_consents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_consents", x => new { x.tenant_id, x.patient_ref });
                    table.CheckConstraint("ck_notification_consents_state", "state IN ('Unknown', 'Granted', 'Revoked')");
                    table.CheckConstraint("ck_notification_consents_version", "version >= 0");
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    appointment_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancellation_cause = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rounds = table.Column<int>(type: "integer", nullable: false),
                    sent_via = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    not_sent_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_kind", "kind IN ('Confirmation', 'Reminder', 'Cancellation')");
                    table.CheckConstraint("ck_notifications_not_sent", "status <> 'NotSent' OR not_sent_reason IS NOT NULL");
                    table.CheckConstraint("ck_notifications_rounds", "rounds >= 0");
                    table.CheckConstraint("ck_notifications_sent", "status <> 'Sent' OR (sent_via IS NOT NULL AND provider_message_id IS NOT NULL)");
                    table.CheckConstraint("ck_notifications_status", "status IN ('Pending', 'Sent', 'NotSent', 'Superseded', 'Expired', 'Failed')");
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trace_parent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_attempts", "attempts >= 0");
                });

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_attempts", x => x.id);
                    table.CheckConstraint("ck_delivery_attempts_channel", "channel IN ('WhatsApp', 'Sms', 'Email')");
                    table.CheckConstraint("ck_delivery_attempts_outcome", "outcome IN ('Delivered', 'Failed', 'CircuitOpen', 'NoContact')");
                    table.ForeignKey(
                        name: "fk_delivery_attempts_notifications",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_jobs",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_jobs", x => x.notification_id);
                    table.ForeignKey(
                        name: "fk_notification_jobs_notifications",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_notification_id",
                table: "delivery_attempts",
                column: "notification_id");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_notification_jobs_due_at",
                table: "notification_jobs",
                column: "due_at");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_tenant_appointment_kind",
                table: "notifications",
                columns: new[] { "tenant_id", "appointment_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_dispatched_at",
                table: "outbox_messages",
                column: "dispatched_at",
                filter: "dispatched_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                columns: new[] { "next_attempt_at", "id" },
                filter: "dispatched_at IS NULL");

            migrationBuilder.EnsureApplicationRole(DatabaseExtensions.ApplicationRole);
            migrationBuilder.EnableTenantRowLevelSecurity(NotificationConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableTenantRowLevelSecurity(DeliveryAttemptConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT");
            migrationBuilder.EnableTenantRowLevelSecurity(AppointmentTrackConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableTenantRowLevelSecurity(NotificationConsentConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableTenantRowLevelSecurity(ClinicSettingsConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableTenantRowLevelSecurity(NotificationJobConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE, DELETE");
            migrationBuilder.EnableOutboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.EnableInboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);

            // O despachante reserva avisos vencidos de todas as clínicas, mas só na fila (sem dados dos avisos).
            migrationBuilder.Sql(
                $"""
                CREATE POLICY {MessagingMigrationExtensions.RelayPolicyName} ON "{NotificationJobConfiguration.TableName}" TO "{DatabaseExtensions.RelayRole}" USING (true) WITH CHECK (true);
                GRANT SELECT, UPDATE ON "{NotificationJobConfiguration.TableName}" TO "{DatabaseExtensions.RelayRole}";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableMessagingSecurity(NotificationJobConfiguration.TableName, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.InboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.OutboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableTenantRowLevelSecurity(ClinicSettingsConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(NotificationConsentConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(AppointmentTrackConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(DeliveryAttemptConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(NotificationConfiguration.TableName, DatabaseExtensions.ApplicationRole);

            migrationBuilder.DropTable(
                name: "appointment_tracks");

            migrationBuilder.DropTable(
                name: "clinic_settings");

            migrationBuilder.DropTable(
                name: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "notification_consents");

            migrationBuilder.DropTable(
                name: "notification_jobs");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
