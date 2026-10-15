using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

namespace CareFlow.BuildingBlocks.Messaging.Persistence;

/// <summary>Permissões de Outbox e Inbox nas migrations dos serviços.</summary>
public static class MessagingMigrationExtensions
{
    public const string RelayPolicyName = "relay_access";

    /// <summary>
    /// Outbox: a API só insere, e só na clínica da sessão (RLS). O papel do despachante lê, atualiza e apaga
    /// de todas as clínicas por uma política própria; ele não tem acesso às tabelas de negócio.
    /// </summary>
    public static void EnableOutboxSecurity(this MigrationBuilder migrationBuilder, string applicationRole, string relayRole)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.EnableTenantRowLevelSecurity(MessagingModelBuilderExtensions.OutboxTable, applicationRole, "INSERT");
        migrationBuilder.GrantRelayAccess(MessagingModelBuilderExtensions.OutboxTable, relayRole, "SELECT, UPDATE, DELETE");
    }

    /// <summary>Inbox: a API lê e insere na clínica da mensagem; o despachante só apaga registros antigos.</summary>
    public static void EnableInboxSecurity(this MigrationBuilder migrationBuilder, string applicationRole, string relayRole)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.EnableTenantRowLevelSecurity(MessagingModelBuilderExtensions.InboxTable, applicationRole, "SELECT, INSERT");
        migrationBuilder.GrantRelayAccess(MessagingModelBuilderExtensions.InboxTable, relayRole, "SELECT, DELETE");
    }

    public static void DisableMessagingSecurity(this MigrationBuilder migrationBuilder, string table, string applicationRole, string relayRole)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(
            $"""
            REVOKE ALL ON "{table}" FROM "{relayRole}";
            DROP POLICY IF EXISTS {RelayPolicyName} ON "{table}";
            """);
        migrationBuilder.DisableTenantRowLevelSecurity(table, applicationRole);
    }

    private static void GrantRelayAccess(this MigrationBuilder migrationBuilder, string table, string relayRole, string privileges)
    {
        migrationBuilder.EnsureApplicationRole(relayRole);
        migrationBuilder.Sql(
            $"""
            CREATE POLICY {RelayPolicyName} ON "{table}" TO "{relayRole}" USING (true) WITH CHECK (true);
            GRANT {privileges} ON "{table}" TO "{relayRole}";
            """);
    }
}
