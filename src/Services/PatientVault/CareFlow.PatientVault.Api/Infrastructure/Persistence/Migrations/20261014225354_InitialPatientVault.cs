using System;

using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.PatientVault.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPatientVault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patients",
                columns: table => new
                {
                    patient_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_version = table.Column<int>(type: "integer", nullable: false),
                    full_name_enc = table.Column<byte[]>(type: "bytea", nullable: false),
                    cpf_enc = table.Column<byte[]>(type: "bytea", nullable: false),
                    birth_date_enc = table.Column<byte[]>(type: "bytea", nullable: false),
                    email_enc = table.Column<byte[]>(type: "bytea", nullable: true),
                    phone_enc = table.Column<byte[]>(type: "bytea", nullable: true),
                    cpf_index = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patients", x => x.patient_ref);
                    table.CheckConstraint("ck_patients_cpf_index_length", "octet_length(cpf_index) = 32");
                });

            migrationBuilder.CreateTable(
                name: "tenant_keys",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    kek_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_keys", x => new { x.tenant_id, x.version });
                    table.CheckConstraint("ck_tenant_keys_version", "version > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ux_patients_tenant_cpf_index",
                table: "patients",
                columns: new[] { "tenant_id", "cpf_index" },
                unique: true);

            // Todo registro aponta para uma chave existente da mesma clínica.
            migrationBuilder.Sql(
                """
                ALTER TABLE patients
                    ADD CONSTRAINT fk_patients_tenant_keys
                    FOREIGN KEY (tenant_id, key_version) REFERENCES tenant_keys (tenant_id, version);
                """);

            // RLS por clínica nas duas tabelas. Chaves são só inseridas e lidas pela API (nunca alteradas);
            // pacientes não têm DELETE: exclusão e crypto-shredding entram com os direitos do titular (fase 8).
            migrationBuilder.EnsureApplicationRole(DatabaseExtensions.ApplicationRole);
            migrationBuilder.EnableTenantRowLevelSecurity(TenantKeyRecordConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT");
            migrationBuilder.EnableTenantRowLevelSecurity(PatientRecordConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity(PatientRecordConfiguration.TableName, DatabaseExtensions.ApplicationRole);
            migrationBuilder.DisableTenantRowLevelSecurity(TenantKeyRecordConfiguration.TableName, DatabaseExtensions.ApplicationRole);

            migrationBuilder.DropTable(
                name: "patients");

            migrationBuilder.DropTable(
                name: "tenant_keys");
        }
    }
}
