using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Audit.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditContentDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "content_digest",
                table: "audit_entries",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_entries_content_digest_length",
                table: "audit_entries",
                sql: "content_digest IS NULL OR octet_length(content_digest) = 32");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_entries_content_digest_length",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "content_digest",
                table: "audit_entries");
        }
    }
}
