using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Persistence.Demo;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace CareFlow.PatientVault.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string ReadScope = "vault.read";
    public const string WriteScope = "vault.write";
    public const string ContactReadScope = "vault.contact.read";

    public const string Read = "Vault.Read";
    public const string Write = "Vault.Write";
    public const string ContactRead = "Vault.ContactRead";

    // Só existe no ambiente de demonstração (Demo:PurgeEnabled): o serviço Demo apaga a clínica sintética da sessão.
    public const string DemoPurge = "Demo.Purge";

    // Diferente da agenda, escrita não implica leitura: quem só cadastra (ex.: integração de recepção)
    // não ganha acesso aos dados pessoais já gravados. Menor privilégio.
    public static AuthorizationBuilder AddVaultPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(DemoPurge, DemoPurgeEndpoint.Scope)
            .AddTenantScopePolicy(Read, ReadScope)
            .AddTenantScopePolicy(Write, WriteScope)
            // Contato é o mínimo para avisar o paciente, com escopo próprio: quem só envia avisos não lê o
            // cadastro inteiro. É a única política que aceita o token de serviço interno.
            .AddPolicy(ContactRead, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, InternalServiceAuthentication.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    ClaimsPrincipalExtensions.TryGetTenantId(context.User, out _)
                    && context.User.HasAnyScope(ContactReadScope)));
}
