using System;
using System.Collections.Generic;

using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Triage.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 7: triagens, projeção do consentimento ai-triage, Outbox e Inbox, todas com RLS por clínica. O
    /// despachante (triage_relay) só enxerga Outbox e Inbox; nunca lê queixas.
    /// </summary>
    public partial class InitialTriage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "triage_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    complaint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    age_band = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    sent_to_model = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    prompt_digest = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    prompt_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    model_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_priority = table.Column<int>(type: "integer", nullable: true),
                    suggested_priority = table.Column<int>(type: "integer", nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rationale = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    guardrails = table.Column<List<string>>(type: "text[]", nullable: false),
                    alarm_signs = table.Column<List<string>>(type: "text[]", nullable: false),
                    masks = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    final_priority = table.Column<int>(type: "integer", nullable: true),
                    reviewed_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_triage_cases", x => x.id);
                    table.CheckConstraint("ck_triage_cases_complaint", "char_length(complaint) BETWEEN 3 AND 1000");
                    table.CheckConstraint("ck_triage_cases_decision", "decision IN ('Pending', 'Confirmed', 'Corrected')");
                    table.CheckConstraint("ck_triage_cases_model_status", "model_status IN ('Accepted', 'SkippedNoConsent', 'SkippedInjection', 'SkippedOverMasked', 'Unavailable', 'Refused', 'RejectedSchema', 'RejectedClinicalLanguage')");
                    table.CheckConstraint("ck_triage_cases_priorities", "suggested_priority BETWEEN 1 AND 4 AND model_priority BETWEEN 1 AND 4 AND final_priority BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_triage_cases_review", "(decision = 'Pending') = (final_priority IS NULL AND reviewed_by IS NULL AND reviewed_at IS NULL)");
                    table.CheckConstraint("ck_triage_cases_sent", "(sent_to_model IS NULL) = (prompt_digest IS NULL)");
                    table.CheckConstraint("ck_triage_cases_source", "source IN ('None', 'Model', 'ModelRaisedByRules', 'Rules')");
                });

            migrationBuilder.CreateTable(
                name: "triage_consents",
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
                    table.PrimaryKey("pk_triage_consents", x => new { x.tenant_id, x.patient_ref });
                    table.CheckConstraint("ck_triage_consents_state", "state IN ('Unknown', 'Granted', 'Revoked')");
                    table.CheckConstraint("ck_triage_consents_version", "version >= 0");
                });

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

            migrationBuilder.CreateIndex(
                name: "ix_triage_cases_tenant_decision_created",
                table: "triage_cases",
                columns: new[] { "tenant_id", "decision", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_triage_cases_tenant_patient_created",
                table: "triage_cases",
                columns: new[] { "tenant_id", "patient_ref", "created_at" });

            migrationBuilder.EnsureApplicationRole(DatabaseExtensions.ApplicationRole);
            migrationBuilder.EnableTenantRowLevelSecurity(TriageCaseConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableTenantRowLevelSecurity(TriageConsentConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableOutboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.EnableInboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.InboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.OutboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableTenantRowLevelSecurity(TriageConsentConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(TriageCaseConfiguration.TableName, DatabaseExtensions.ApplicationRole);

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "triage_cases");

            migrationBuilder.DropTable(
                name: "triage_consents");
        }
    }
}
