using CareFlow.BuildingBlocks.AspNetCore.Security;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.Consent.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string ReadScope = "consent.read";
    public const string WriteScope = "consent.write";

    public const string Read = "Consent.Read";
    public const string Write = "Consent.Write";

    public static AuthorizationBuilder AddConsentPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(Read, ReadScope, WriteScope)
            .AddTenantScopePolicy(Write, WriteScope);
}
