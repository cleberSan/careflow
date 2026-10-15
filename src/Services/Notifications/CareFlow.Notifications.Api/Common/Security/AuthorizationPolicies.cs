using CareFlow.BuildingBlocks.AspNetCore.Security;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.Notifications.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string ReadScope = "notifications.read";
    public const string AdminScope = "notifications.admin";

    public const string Read = "Notifications.Read";
    public const string Admin = "Notifications.Admin";

    // Quem configura a clínica também enxerga o histórico de envios dela; o contrário não vale.
    public static AuthorizationBuilder AddNotificationsPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(Read, ReadScope, AdminScope)
            .AddTenantScopePolicy(Admin, AdminScope);
}
