using Microsoft.EntityFrameworkCore.Migrations;

namespace CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

/// <summary>
/// SQL de Row-Level Security reutilizado pelas migrations dos serviços.
/// </summary>
public static class RowLevelSecurityMigrationExtensions
{
    public const string PolicyName = "tenant_isolation";

    /// <summary>
    /// Cria o papel da aplicação (sem login) se ele ainda não existir e concede a ele só USAGE no schema.
    /// O login real da API é criado pela infraestrutura, com senha fora do repositório, como membro deste papel.
    /// </summary>
    public static void EnsureApplicationRole(this MigrationBuilder migrationBuilder, string role)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var quoted = QuoteIdentifier(role);
        var literal = role.Replace("'", "''", StringComparison.Ordinal);

        migrationBuilder.Sql(
            $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{literal}') THEN
                    CREATE ROLE {quoted} NOLOGIN NOSUPERUSER NOBYPASSRLS;
                END IF;
            END
            $$;
            GRANT USAGE ON SCHEMA public TO {quoted};
            """);
    }

    /// <summary>
    /// Liga RLS na tabela (inclusive para o dono, com FORCE), cria a política por clínica e concede ao papel
    /// da aplicação apenas os comandos informados. A política vale para leitura (USING) e escrita (WITH CHECK):
    /// a aplicação não consegue ler nem gravar linhas de outra clínica, mesmo com um bug no código.
    /// </summary>
    public static void EnableTenantRowLevelSecurity(
        this MigrationBuilder migrationBuilder,
        string table,
        string role,
        string privileges = "SELECT, INSERT, UPDATE",
        string tenantColumn = "tenant_id")
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var quotedTable = QuoteIdentifier(table);
        var quotedColumn = QuoteIdentifier(tenantColumn);
        var predicate = $"{quotedColumn} = NULLIF(current_setting('{TenantSessionInterceptor.SettingName}', true), '')::uuid";

        migrationBuilder.Sql(
            $"""
            ALTER TABLE {quotedTable} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {quotedTable} FORCE ROW LEVEL SECURITY;
            CREATE POLICY {PolicyName} ON {quotedTable}
                USING ({predicate})
                WITH CHECK ({predicate});
            GRANT {privileges} ON {quotedTable} TO {QuoteIdentifier(role)};
            """);
    }

    public static void DisableTenantRowLevelSecurity(this MigrationBuilder migrationBuilder, string table, string role)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var quotedTable = QuoteIdentifier(table);

        migrationBuilder.Sql(
            $"""
            REVOKE ALL ON {quotedTable} FROM {QuoteIdentifier(role)};
            DROP POLICY IF EXISTS {PolicyName} ON {quotedTable};
            ALTER TABLE {quotedTable} NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE {quotedTable} DISABLE ROW LEVEL SECURITY;
            """);
    }

    // Identificadores vêm de constantes do código, nunca de entrada externa; ainda assim só aceitamos
    // o formato simples para que um nome inesperado quebre a migration em vez de virar SQL arbitrário.
    private static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        foreach (var c in identifier)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                throw new ArgumentException($"Identificador SQL inválido: '{identifier}'.", nameof(identifier));
            }
        }

        return '"' + identifier + '"';
    }
}
