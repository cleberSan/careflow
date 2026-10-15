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
    public const string TriageProfileReadScope = "vault.triage-profile.read";

    public const string Read = "Vault.Read";
    public const string Write = "Vault.Write";
    public const string ContactRead = "Vault.ContactRead";
    public const string TriageProfileRead = "Vault.TriageProfileRead";

    // Só existe no ambiente de demonstração (Demo:PurgeEnabled): o serviço Demo apaga a clínica sintética da sessão.
    public const string DemoPurge = "Demo.Purge";

    // Diferente da agenda, escrita não implica leitura: quem só cadastra (ex.: integração de recepção)
    // não ganha acesso aos dados pessoais já gravados. Menor privilégio.
    public static AuthorizationBuilder AddVaultPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(DemoPurge, DemoPurgeEndpoint.Scope)
            .AddTenantScopePolicy(Read, ReadScope)
            .AddTenantScopePolicy(Write, WriteScope)
            // Contato e perfil de triagem são o mínimo de cada serviço, com escopo próprio: quem só envia avisos
            // não lê o cadastro inteiro, e a triagem não lê contato. São as únicas políticas que aceitam o token
            // de serviço interno.
            .AddServicePolicy(ContactRead, ContactReadScope)
            .AddServicePolicy(TriageProfileRead, TriageProfileReadScope);

    private static AuthorizationBuilder AddServicePolicy(this AuthorizationBuilder builder, string name, string scope) =>
        builder.AddPolicy(name, policy => policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, InternalServiceAuthentication.SchemeName)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                ClaimsPrincipalExtensions.TryGetTenantId(context.User, out _)
                && context.User.HasAnyScope(scope)));
}
