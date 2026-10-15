using System;

using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Audit.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_chain_heads",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_chain_heads", x => x.tenant_id);
                    table.CheckConstraint("ck_audit_chain_heads_hash_length", "octet_length(hash) = 32");
                    table.CheckConstraint("ck_audit_chain_heads_sequence", "sequence >= 0");
                });

            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_service = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    previous_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => new { x.tenant_id, x.sequence });
                    table.CheckConstraint("ck_audit_entries_hash_length", "octet_length(hash) = 32 AND octet_length(previous_hash) = 32");
                    table.CheckConstraint("ck_audit_entries_sequence", "sequence > 0");
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
                name: "ix_audit_entries_tenant_resource_sequence",
                table: "audit_entries",
                columns: new[] { "tenant_id", "resource_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ux_audit_entries_tenant_message",
                table: "audit_entries",
                columns: new[] { "tenant_id", "message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

            // Append-only no próprio banco: UPDATE, DELETE e TRUNCATE são recusados para qualquer papel, inclusive
            // o dono. Só quem desliga o gatilho (dono do schema) consegue alterar, e a cadeia de hash denuncia.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION audit_entries_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_entries é append-only: % não é permitido', TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END
                $$;

                CREATE TRIGGER tr_audit_entries_append_only
                    BEFORE UPDATE OR DELETE ON audit_entries
                    FOR EACH ROW EXECUTE FUNCTION audit_entries_append_only();

                CREATE TRIGGER tr_audit_entries_no_truncate
                    BEFORE TRUNCATE ON audit_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_entries_append_only();
                """);

            // RLS por clínica. Entradas: só INSERT e SELECT. Cabeça: também UPDATE (e o FOR UPDATE que a trava).
            migrationBuilder.EnsureApplicationRole(DatabaseExtensions.ApplicationRole);
            migrationBuilder.EnableTenantRowLevelSecurity(AuditEntryConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT");
            migrationBuilder.EnableTenantRowLevelSecurity(ChainHeadConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
            migrationBuilder.EnableInboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.InboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableTenantRowLevelSecurity(ChainHeadConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(AuditEntryConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.Sql(
                """
                DROP TRIGGER tr_audit_entries_no_truncate ON audit_entries;
                DROP TRIGGER tr_audit_entries_append_only ON audit_entries;
                DROP FUNCTION audit_entries_append_only();
                """);

            migrationBuilder.DropTable(
                name: "audit_chain_heads");

            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "inbox_messages");
        }
    }
}
