using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Persistence.Demo;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.Scheduling.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string ReadScope = "scheduling.read";
    public const string WriteScope = "scheduling.write";

    public const string Read = "Scheduling.Read";
    public const string Write = "Scheduling.Write";

    // Só existe no ambiente de demonstração (Demo:PurgeEnabled): o serviço Demo apaga a clínica sintética da sessão.
    public const string DemoPurge = "Demo.Purge";

    public static AuthorizationBuilder AddSchedulingPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(DemoPurge, DemoPurgeEndpoint.Scope)
            .AddTenantScopePolicy(Read, ReadScope, WriteScope)
            .AddTenantScopePolicy(Write, WriteScope);
}
