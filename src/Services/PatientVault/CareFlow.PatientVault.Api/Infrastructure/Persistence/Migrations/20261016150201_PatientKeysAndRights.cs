using System;

using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.PatientVault.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 8 (ADR-0012): chave por paciente, eliminação e Inbox para os pedidos do titular. Só acrescenta: colunas
    /// novas com padrão ou nulas e tabelas novas, sem reescrever linhas. Os registros existentes ficam no esquema 1
    /// (chave da clínica) e migram depois, em lotes, pela manutenção de chaves, com o cofre no ar.
    /// </summary>
    public partial class PatientKeysAndRights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<byte[]>(
                name: "cpf_index",
                table: "patients",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "patients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "key_scheme",
                table: "patients",
                type: "smallint",
                nullable: false,
                // Linhas existentes (e as que uma instância da versão anterior gravar durante o deploy) são do
                // esquema 1. A versão nova sempre informa o esquema.
                defaultValue: (short)1);

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
                name: "patient_keys",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_key_version = table.Column<int>(type: "integer", nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patient_keys", x => new { x.tenant_id, x.patient_ref });
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_at",
                table: "inbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_patient_keys_tenant_key_version",
                table: "patient_keys",
                columns: new[] { "tenant_id", "tenant_key_version" });

            migrationBuilder.Sql(
                """
                ALTER TABLE patients
                    ADD CONSTRAINT ck_patients_key_scheme CHECK (key_scheme IN (1, 2));
                ALTER TABLE patient_keys
                    ADD CONSTRAINT ck_patient_keys_wrapped_key_length CHECK (octet_length(wrapped_key) > 28);
                -- A chave do paciente aponta para a versão da chave da clínica que a embrulha: essa versão não pode
                -- ser destruída enquanto embrulhar alguma chave.
                ALTER TABLE patient_keys
                    ADD CONSTRAINT fk_patient_keys_tenant_keys
                    FOREIGN KEY (tenant_id, tenant_key_version) REFERENCES tenant_keys (tenant_id, version);
                -- Destruir versões antigas da chave da clínica (rotação) e chaves de paciente (eliminação).
                GRANT DELETE ON tenant_keys TO vault_app;
                """);

            migrationBuilder.EnableTenantRowLevelSecurity(PatientKeyRecordConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE, DELETE");
            migrationBuilder.EnableInboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.InboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
            migrationBuilder.DisableTenantRowLevelSecurity(PatientKeyRecordConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.Sql(
                """
                REVOKE DELETE ON tenant_keys FROM vault_app;
                ALTER TABLE patients DROP CONSTRAINT IF EXISTS ck_patients_key_scheme;
                """);

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "patient_keys");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "patients");

            migrationBuilder.DropColumn(
                name: "key_scheme",
                table: "patients");

            migrationBuilder.AlterColumn<byte[]>(
                name: "cpf_index",
                table: "patients",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);
        }
    }
}
