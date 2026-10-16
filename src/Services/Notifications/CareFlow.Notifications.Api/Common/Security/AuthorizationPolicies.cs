using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Persistence.Demo;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.Notifications.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string ReadScope = "notifications.read";
    public const string AdminScope = "notifications.admin";

    public const string DemoInboxScope = "notifications.demo-inbox";

    public const string Read = "Notifications.Read";
    public const string Admin = "Notifications.Admin";

    // Só existe no ambiente de demonstração (Demo:PurgeEnabled): o serviço Demo apaga a clínica sintética da sessão.
    public const string DemoPurge = "Demo.Purge";

    // Só existe com Demo:SimulatedInboxEnabled: o Painel lê o código de confirmação no provedor simulado.
    public const string DemoInbox = "Demo.Inbox";

    // Quem configura a clínica também enxerga o histórico de envios dela; o contrário não vale.
    public static AuthorizationBuilder AddNotificationsPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(DemoPurge, DemoPurgeEndpoint.Scope)
            .AddTenantScopePolicy(DemoInbox, DemoInboxScope)
            .AddTenantScopePolicy(Read, ReadScope, AdminScope)
            .AddTenantScopePolicy(Admin, AdminScope);
}
