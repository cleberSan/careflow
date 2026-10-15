using System;

using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Scheduling.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 4: projeção de pacientes alimentada por eventos, motivo do cancelamento, Outbox e Inbox. Todas as
    /// tabelas novas com RLS por clínica; o despachante (scheduling_relay) só enxerga Outbox e Inbox.
    /// </summary>
    public partial class PatientProjectionAndMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                table: "appointments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

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
                name: "patient_statuses",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    is_registered = table.Column<bool>(type: "boolean", nullable: false),
                    scheduling_consent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    consent_version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patient_statuses", x => new { x.tenant_id, x.patient_ref });
                    table.CheckConstraint("ck_patient_statuses_consent", "scheduling_consent IN ('Unknown', 'Granted', 'Revoked')");
                    table.CheckConstraint("ck_patient_statuses_consent_version", "consent_version >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_tenant_patient_starts_at",
                table: "appointments",
                columns: new[] { "tenant_id", "patient_ref", "starts_at" },
                filter: "status = 'Booked'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_appointments_cancellation_reason",
                table: "appointments",
                sql: "cancellation_reason IS NULL OR cancellation_reason IN ('Requested', 'ConsentRevoked')");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

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

            // Cancelamentos anteriores à fase 4 foram todos a pedido. O dono da tabela também está sujeito ao
            // RLS (FORCE), então a correção roda com a política suspensa, dentro da transação da migration.
            migrationBuilder.Sql(
                """
                ALTER TABLE appointments NO FORCE ROW LEVEL SECURITY;
                UPDATE appointments SET cancellation_reason = 'Requested' WHERE status = 'Cancelled' AND cancellation_reason IS NULL;
                ALTER TABLE appointments FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.EnableTenantRowLevelSecurity(PatientStatusConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableOutboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.EnableInboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.InboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.OutboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableTenantRowLevelSecurity(PatientStatusConfiguration.TableName, DatabaseExtensions.ApplicationRole);

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "patient_statuses");

            migrationBuilder.DropIndex(
                name: "ix_appointments_tenant_patient_starts_at",
                table: "appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_appointments_cancellation_reason",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                table: "appointments");
        }
    }
}
