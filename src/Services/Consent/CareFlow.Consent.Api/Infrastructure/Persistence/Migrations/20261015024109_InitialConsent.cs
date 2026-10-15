using System;

using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Consent.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    legal_basis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    term_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consents", x => x.id);
                    table.CheckConstraint("ck_consents_revoked_at", "(status = 'Revoked') = (revoked_at IS NOT NULL)");
                    table.CheckConstraint("ck_consents_status", "status IN ('Granted', 'Revoked')");
                    table.CheckConstraint("ck_consents_version", "version > 0");
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

            migrationBuilder.CreateIndex(
                name: "ux_consents_tenant_patient_purpose",
                table: "consents",
                columns: new[] { "tenant_id", "patient_ref", "purpose" },
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

            // RLS por clínica. Consentimentos sem DELETE: a revogação é uma mudança de estado, e o histórico de
            // concessões e revogações fica na trilha de auditoria. O Outbox é só INSERT para a API.
            migrationBuilder.EnsureApplicationRole(DatabaseExtensions.ApplicationRole);
            migrationBuilder.EnableTenantRowLevelSecurity(ConsentConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableOutboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.OutboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableTenantRowLevelSecurity(ConsentConfiguration.TableName, DatabaseExtensions.ApplicationRole);

            migrationBuilder.DropTable(
                name: "consents");

            migrationBuilder.DropTable(
                name: "outbox_messages");
        }
    }
}
