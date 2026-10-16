using System;


using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Triage.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 8 (ADR-0012): eliminação a pedido do titular. A queixa passa a aceitar nulo depois da eliminação, junto
    /// com o texto enviado ao modelo e a justificativa; a decisão e o resumo SHA-256 ficam. Só afrouxa restrições.
    /// </summary>
    public partial class SubjectErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_triage_cases_complaint",
                table: "triage_cases");

            migrationBuilder.DropCheckConstraint(
                name: "ck_triage_cases_sent",
                table: "triage_cases");

            migrationBuilder.AlterColumn<string>(
                name: "complaint",
                table: "triage_cases",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "triage_cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_triage_cases_complaint",
                table: "triage_cases",
                sql: "(complaint IS NULL AND erased_at IS NOT NULL) OR char_length(complaint) BETWEEN 3 AND 1000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_triage_cases_sent",
                table: "triage_cases",
                sql: "(sent_to_model IS NULL) = (prompt_digest IS NULL) OR erased_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_triage_cases_complaint",
                table: "triage_cases");

            migrationBuilder.DropCheckConstraint(
                name: "ck_triage_cases_sent",
                table: "triage_cases");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "triage_cases");

            migrationBuilder.AlterColumn<string>(
                name: "complaint",
                table: "triage_cases",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_triage_cases_complaint",
                table: "triage_cases",
                sql: "char_length(complaint) BETWEEN 3 AND 1000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_triage_cases_sent",
                table: "triage_cases",
                sql: "(sent_to_model IS NULL) = (prompt_digest IS NULL)");
        }
    }
}
