using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CareFlow.BuildingBlocks.AspNetCore.Tenancy;

/// <summary>
/// Resolve a clínica a partir da claim do token. Fora de uma requisição HTTP (consumidor de mensagens),
/// usa a clínica do <see cref="TenantScope"/>. Dentro de uma requisição, só o token vale: o escopo nunca
/// sobrepõe a claim. Falha fechada: sem clínica válida, lança exceção em vez de devolver um tenant vazio.
/// </summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor, TenantScope scope) : ITenantContext
{
    public Guid TenantId =>
        TryGetTenantId(out var tenantId)
            ? tenantId
            : throw new InvalidOperationException("A requisição não tem uma clínica (tenant) válida.");

    public bool TryGetTenantId(out Guid tenantId)
    {
        var context = accessor.HttpContext;
        if (context is not null)
        {
            return ClaimsPrincipalExtensions.TryGetTenantId(context.User, out tenantId);
        }

        return scope.TryGetTenantId(out tenantId);
    }
}

public static class HttpTenantContextExtensions
{
    public static IServiceCollection AddHttpTenantContext(this IServiceCollection services) =>
        services
            .AddHttpContextAccessor()
            .AddScoped<TenantScope>()
            .AddScoped<ITenantContext, HttpTenantContext>();
}
