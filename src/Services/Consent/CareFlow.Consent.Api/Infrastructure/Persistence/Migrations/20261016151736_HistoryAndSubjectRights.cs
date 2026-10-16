using System;

using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Consent.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 8 (ADR-0012): histórico de consentimentos (prova do consentimento), pseudônimos eliminados e Inbox
    /// para os pedidos do titular. Os consentimentos existentes entram no histórico com a situação atual: as mudanças
    /// anteriores não foram guardadas (até aqui o Outbox era o único registro, e ele é limpo em 7 dias).
    /// </summary>
    public partial class HistoryAndSubjectRights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consent_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    legal_basis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    term_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    change = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent_history", x => x.id);
                    table.CheckConstraint("ck_consent_history_change", "change IN ('Granted', 'Revoked')");
                    table.CheckConstraint("ck_consent_history_version", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "erased_subjects",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    erased_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erased_subjects", x => new { x.tenant_id, x.patient_ref });
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

            migrationBuilder.CreateIndex(
                name: "ix_consent_history_patient",
                table: "consent_history",
                columns: new[] { "tenant_id", "patient_ref", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ux_consent_history_consent_version",
                table: "consent_history",
                columns: new[] { "consent_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

            migrationBuilder.Sql(
                """
                INSERT INTO consent_history (id, tenant_id, consent_id, patient_ref, purpose, legal_basis, term_version, change, version, occurred_at)
                SELECT gen_random_uuid(), tenant_id, id, patient_ref, purpose, legal_basis, term_version, status, version,
                       CASE WHEN status = 'Revoked' THEN revoked_at ELSE granted_at END
                FROM consents;
                """);

            // O histórico só cresce: sem UPDATE nem DELETE para a API. Pseudônimos eliminados também não voltam.
            migrationBuilder.EnableTenantRowLevelSecurity(ConsentHistoryConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT");
            migrationBuilder.EnableTenantRowLevelSecurity(ErasedSubjectConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT");
            migrationBuilder.EnableInboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.InboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableTenantRowLevelSecurity(ErasedSubjectConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(ConsentHistoryConfiguration.TableName, DatabaseExtensions.ApplicationRole);

            migrationBuilder.DropTable(
                name: "consent_history");

            migrationBuilder.DropTable(
                name: "erased_subjects");

            migrationBuilder.DropTable(
                name: "inbox_messages");
        }
    }
}
