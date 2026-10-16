using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Persistence.Demo;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.Rights.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string ReadScope = "rights.read";
    public const string WriteScope = "rights.write";

    /// <summary>Reaplicar eliminações depois de restaurar um backup: operação da clínica, não do atendimento.</summary>
    public const string AdminScope = "rights.admin";

    public const string Read = "Rights.Read";
    public const string Write = "Rights.Write";
    public const string Admin = "Rights.Admin";

    public const string DemoPurge = "Demo.Purge";

    public static AuthorizationBuilder AddRightsPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(DemoPurge, DemoPurgeEndpoint.Scope)
            .AddTenantScopePolicy(Read, ReadScope, WriteScope)
            .AddTenantScopePolicy(Write, WriteScope)
            .AddTenantScopePolicy(Admin, AdminScope);
}
