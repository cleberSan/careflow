using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CareFlow.BuildingBlocks.AspNetCore.Tenancy;

/// <summary>
/// Resolve a clínica a partir da claim do token. Falha fechada: sem claim válida, lança exceção
/// em vez de devolver um tenant vazio que poderia vazar dados entre clínicas.
/// </summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid TenantId =>
        TryGetTenantId(out var tenantId)
            ? tenantId
            : throw new InvalidOperationException("A requisição não tem uma clínica (tenant) válida.");

    public bool TryGetTenantId(out Guid tenantId) =>
        ClaimsPrincipalExtensions.TryGetTenantId(accessor.HttpContext?.User, out tenantId);
}

public static class HttpTenantContextExtensions
{
    public static IServiceCollection AddHttpTenantContext(this IServiceCollection services) =>
        services
            .AddHttpContextAccessor()
            .AddScoped<ITenantContext, HttpTenantContext>();
}
