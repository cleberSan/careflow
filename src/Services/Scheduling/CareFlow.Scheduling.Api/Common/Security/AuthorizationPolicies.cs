using CareFlow.BuildingBlocks.AspNetCore.Security;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.Scheduling.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string ReadScope = "scheduling.read";
    public const string WriteScope = "scheduling.write";

    public const string Read = "Scheduling.Read";
    public const string Write = "Scheduling.Write";

    public static AuthorizationBuilder AddSchedulingPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(Read, ReadScope, WriteScope)
            .AddTenantScopePolicy(Write, WriteScope);
}
