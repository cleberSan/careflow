using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Scheduling.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 8 (ADR-0012): eliminação a pedido do titular. A projeção do paciente guarda quando ele foi eliminado
    /// (bloqueia novas reservas) e as consultas futuras canceladas ganham o motivo próprio. Só acrescenta.
    /// </summary>
    public partial class SubjectErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_appointments_cancellation_reason",
                table: "appointments");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "patient_statuses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_appointments_cancellation_reason",
                table: "appointments",
                sql: "cancellation_reason IS NULL OR cancellation_reason IN ('Requested', 'ConsentRevoked', 'SubjectErasure')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_appointments_cancellation_reason",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "patient_statuses");

            migrationBuilder.AddCheckConstraint(
                name: "ck_appointments_cancellation_reason",
                table: "appointments",
                sql: "cancellation_reason IS NULL OR cancellation_reason IN ('Requested', 'ConsentRevoked')");
        }
    }
}
