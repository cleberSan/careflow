using System;


using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Notifications.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 8 (ADR-0012): eliminação a pedido do titular. O aviso guarda quando o titular foi eliminado e pode ficar
    /// como enviado sem a referência ao provedor, que é apagada. Só acrescenta.
    /// </summary>
    public partial class SubjectErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_sent",
                table: "notifications");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_sent",
                table: "notifications",
                sql: "status <> 'Sent' OR (sent_via IS NOT NULL AND (provider_message_id IS NOT NULL OR erased_at IS NOT NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_sent",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "notifications");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_sent",
                table: "notifications",
                sql: "status <> 'Sent' OR (sent_via IS NOT NULL AND provider_message_id IS NOT NULL)");
        }
    }
}
