using System;

using CareFlow.BuildingBlocks.Messaging.Persistence;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.PatientVault.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 4: Outbox do cofre (cadastro de paciente e registros de acesso). A API só insere, na clínica da
    /// sessão; o despachante (vault_relay) lê e marca o que publicou, sem acesso a pacientes ou chaves.
    /// </summary>
    public partial class VaultOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trace_parent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_attempts", "attempts >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_dispatched_at",
                table: "outbox_messages",
                column: "dispatched_at",
                filter: "dispatched_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                columns: new[] { "next_attempt_at", "id" },
                filter: "dispatched_at IS NULL");

            migrationBuilder.EnableOutboxSecurity(DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableMessagingSecurity(MessagingModelBuilderExtensions.OutboxTable, DatabaseExtensions.ApplicationRole, DatabaseExtensions.RelayRole);

            migrationBuilder.DropTable(
                name: "outbox_messages");
        }
    }
}
