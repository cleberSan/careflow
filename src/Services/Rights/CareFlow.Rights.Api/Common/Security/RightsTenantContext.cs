using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Tenancy;

namespace CareFlow.Rights.Api.Common.Security;

/// <summary>
/// Clínica da operação. Requisição autenticada: só a claim do token vale, como nos outros serviços. Requisição
/// anônima (o download pelo link, que o titular abre sem conta) ou consumidor de mensagens: a clínica do
/// <see cref="TenantScope"/>, que só o código do download e o processador de Inbox preenchem. Falha fechada.
/// </summary>
internal sealed class RightsTenantContext(IHttpContextAccessor accessor, TenantScope scope) : ITenantContext
{
    public Guid TenantId =>
        TryGetTenantId(out var tenantId)
            ? tenantId
            : throw new InvalidOperationException("A operação não tem uma clínica (tenant) válida.");

    public bool TryGetTenantId(out Guid tenantId)
    {
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            return ClaimsPrincipalExtensions.TryGetTenantId(user, out tenantId);
        }

        return scope.TryGetTenantId(out tenantId);
    }
}

internal static class RightsTenantContextExtensions
{
    public static IServiceCollection AddRightsTenantContext(this IServiceCollection services) =>
        services
            .AddHttpContextAccessor()
            .AddScoped<TenantScope>()
            .AddScoped<ITenantContext, RightsTenantContext>();
}
