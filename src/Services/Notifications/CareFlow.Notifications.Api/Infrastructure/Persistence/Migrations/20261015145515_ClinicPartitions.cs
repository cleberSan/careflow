using System;
using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Notifications.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 6: partições isoladas de circuit breaker para as clínicas sintéticas do Painel. RLS como as demais; o
    /// índice único da partição vale entre clínicas mesmo sem uma enxergar a linha da outra.
    /// </summary>
    public partial class ClinicPartitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clinic_partitions",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clinic_partitions", x => x.tenant_id);
                    table.CheckConstraint("ck_clinic_partitions_slot", "slot BETWEEN 1 AND 64");
                });

            migrationBuilder.CreateIndex(
                name: "ux_clinic_partitions_slot",
                table: "clinic_partitions",
                column: "slot",
                unique: true);

            migrationBuilder.EnableTenantRowLevelSecurity(
                ClinicPartitionConfiguration.TableName, DatabaseExtensions.ApplicationRole, "SELECT, INSERT, UPDATE, DELETE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clinic_partitions");
        }
    }
}
