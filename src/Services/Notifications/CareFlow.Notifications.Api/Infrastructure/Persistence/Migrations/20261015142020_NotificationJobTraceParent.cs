using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Notifications.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationJobTraceParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                table: "notification_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_parent",
                table: "notification_jobs");
        }
    }
}
