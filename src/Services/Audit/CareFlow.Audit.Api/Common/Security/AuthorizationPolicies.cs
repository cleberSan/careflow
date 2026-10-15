using CareFlow.BuildingBlocks.AspNetCore.Security;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.Audit.Api.Common.Security;

internal static class AuthorizationPolicies
{
    /// <summary>Leitura da trilha e verificação da cadeia. Não há escopo de escrita: só eventos gravam.</summary>
    public const string ReadScope = "audit.read";

    public const string Read = "Audit.Read";

    public static AuthorizationBuilder AddAuditPolicies(this AuthorizationBuilder builder) =>
        builder.AddTenantScopePolicy(Read, ReadScope);
}
