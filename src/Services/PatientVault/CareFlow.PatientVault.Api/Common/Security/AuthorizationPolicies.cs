using CareFlow.BuildingBlocks.AspNetCore.Security;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.PatientVault.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string ReadScope = "vault.read";
    public const string WriteScope = "vault.write";

    public const string Read = "Vault.Read";
    public const string Write = "Vault.Write";

    // Diferente da agenda, escrita não implica leitura: quem só cadastra (ex.: integração de recepção)
    // não ganha acesso aos dados pessoais já gravados. Menor privilégio.
    public static AuthorizationBuilder AddVaultPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(Read, ReadScope)
            .AddTenantScopePolicy(Write, WriteScope);
}
