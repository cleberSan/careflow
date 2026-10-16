using System;

using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Rights.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 8 (ADR-0012): serviço de pedidos do titular. Pedidos, respostas dos serviços, pacotes cifrados e links
    /// com RLS por clínica; o índice de expiração dos pacotes fica sem RLS e só com identificadores.
    /// </summary>
    public partial class InitialRights : Migration
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
                name: "package_expirations",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_package_expirations", x => x.request_id);
                });

            migrationBuilder.CreateTable(
                name: "subject_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    denial_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    code_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    code_attempts = table.Column<int>(type: "integer", nullable: false),
                    code_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    code_channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    identity_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    policy_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    wrapped_recipient_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subject_requests", x => x.id);
                    table.CheckConstraint("ck_subject_requests_code_attempts", "code_attempts BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_subject_requests_code_hash", "code_hash IS NULL OR octet_length(code_hash) = 32");
                    table.CheckConstraint("ck_subject_requests_code_only_while_received", "code_hash IS NULL OR status = 'Received'");
                    table.CheckConstraint("ck_subject_requests_denial", "(status = 'Denied') = (denial_reason IS NOT NULL)");
                    table.CheckConstraint("ck_subject_requests_key_only_while_open", "wrapped_recipient_key IS NULL OR status = 'InProgress'");
                    table.CheckConstraint("ck_subject_requests_kind", "kind IN ('Access', 'Portability', 'Erasure')");
                    table.CheckConstraint("ck_subject_requests_status", "status IN ('Received', 'InProgress', 'Completed', 'Denied')");
                });

            migrationBuilder.CreateTable(
                name: "download_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_links", x => x.id);
                    table.CheckConstraint("ck_download_links_token_hash", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "fk_download_links_requests",
                        column: x => x.request_id,
                        principalTable: "subject_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subject_request_answers",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sealed_section = table.Column<string>(type: "text", nullable: true),
                    erasure = table.Column<string>(type: "jsonb", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subject_request_answers", x => new { x.request_id, x.service });
                    table.ForeignKey(
                        name: "fk_subject_request_answers_requests",
                        column: x => x.request_id,
                        principalTable: "subject_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subject_request_packages",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: true),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    destroyed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subject_request_packages", x => x.request_id);
                    table.CheckConstraint("ck_subject_request_packages_destroyed", "(destroyed_at IS NULL) = (content IS NOT NULL AND wrapped_key IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_subject_request_packages_requests",
                        column: x => x.request_id,
                        principalTable: "subject_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_download_links_request_id",
                table: "download_links",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ux_download_links_token_hash",
                table: "download_links",
                column: "token_hash",
                unique: true);

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
                name: "ix_package_expirations_expires_at",
                table: "package_expirations",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_subject_request_packages_expiring",
                table: "subject_request_packages",
                column: "expires_at",
                filter: "destroyed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_subject_requests_open",
                table: "subject_requests",
                columns: new[] { "tenant_id", "patient_ref", "kind" },
                unique: true,
                filter: "status IN ('Received', 'InProgress')");

            migrationBuilder.EnsureApplicationRole(DatabaseExtensions.ApplicationRole);
            migrationBuilder.EnableTenantRowLevelSecurity(SubjectRequestConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableTenantRowLevelSecurity(SubjectRequestAnswerConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableTenantRowLevelSecurity(SubjectRequestPackageConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableTenantRowLevelSecurity(DownloadLinkConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE, DELETE");

            // Sem RLS de propósito: a limpeza precisa achar os pacotes vencidos de todas as clínicas (PackageExpiration).
            migrationBuilder.Sql($"GRANT SELECT, INSERT, DELETE ON {PackageExpirationConfiguration.TableName} TO {DatabaseExtensions.ApplicationRole};");

            migrationBuilder.EnableOutboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.EnableInboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.InboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.OutboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.Sql($"REVOKE ALL ON {PackageExpirationConfiguration.TableName} FROM {DatabaseExtensions.ApplicationRole};");
            migrationBuilder.DisableTenantRowLevelSecurity(DownloadLinkConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(SubjectRequestPackageConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(SubjectRequestAnswerConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(SubjectRequestConfiguration.TableName, DatabaseExtensions.ApplicationRole);

            migrationBuilder.DropTable(
                name: "download_links");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "package_expirations");

            migrationBuilder.DropTable(
                name: "subject_request_answers");

            migrationBuilder.DropTable(
                name: "subject_request_packages");

            migrationBuilder.DropTable(
                name: "subject_requests");
        }
    }
}
