using Microsoft.AspNetCore.Authorization;

namespace CareFlow.BuildingBlocks.AspNetCore.Security;

public static class TenantScopePolicyExtensions
{
    /// <summary>
    /// Política que exige usuário autenticado, uma clínica válida no token e pelo menos um dos escopos.
    /// Sem clínica a requisição é recusada antes de chegar ao handler (falha fechada).
    /// </summary>
    public static AuthorizationBuilder AddTenantScopePolicy(
        this AuthorizationBuilder builder,
        string name,
        params string[] anyOfScopes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (anyOfScopes is null || anyOfScopes.Length == 0)
        {
            throw new ArgumentException("Informe pelo menos um escopo.", nameof(anyOfScopes));
        }

        return builder.AddPolicy(name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                ClaimsPrincipalExtensions.TryGetTenantId(context.User, out _)
                && context.User.HasAnyScope(anyOfScopes)));
    }
}
