using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Persistence.Demo;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.Consent.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string ReadScope = "consent.read";
    public const string WriteScope = "consent.write";

    public const string Read = "Consent.Read";
    public const string Write = "Consent.Write";

    // Só existe no ambiente de demonstração (Demo:PurgeEnabled): o serviço Demo apaga a clínica sintética da sessão.
    public const string DemoPurge = "Demo.Purge";

    public static AuthorizationBuilder AddConsentPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(DemoPurge, DemoPurgeEndpoint.Scope)
            .AddTenantScopePolicy(Read, ReadScope, WriteScope)
            .AddTenantScopePolicy(Write, WriteScope);
}
