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
            .AddPolicy(Read, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => HasTenant(ctx) && ctx.User.HasAnyScope(ReadScope, WriteScope)))
            .AddPolicy(Write, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => HasTenant(ctx) && ctx.User.HasAnyScope(WriteScope)));

    private static bool HasTenant(AuthorizationHandlerContext context) =>
        ClaimsPrincipalExtensions.TryGetTenantId(context.User, out _);
}
