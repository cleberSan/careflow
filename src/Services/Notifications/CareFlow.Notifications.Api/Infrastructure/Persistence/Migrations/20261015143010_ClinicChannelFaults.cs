using System;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Notifications.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>Fase 6: falhas injetadas por clínica isolada (demo), com RLS como as demais tabelas.</summary>
    public partial class ClinicChannelFaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clinic_channel_faults",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clinic_channel_faults", x => new { x.tenant_id, x.channel });
                    table.CheckConstraint("ck_clinic_channel_faults_mode", "mode IN ('Latency', 'Error', 'Unavailable', 'LostResponse')");
                });

            migrationBuilder.EnableTenantRowLevelSecurity(
                ClinicChannelFaultConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE, DELETE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clinic_channel_faults");
        }
    }
}
