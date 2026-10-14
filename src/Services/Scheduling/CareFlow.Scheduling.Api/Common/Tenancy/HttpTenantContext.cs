using CareFlow.Scheduling.Api.Common.Security;

namespace CareFlow.Scheduling.Api.Common.Tenancy;

/// <summary>
/// Resolve a clínica a partir da claim do token. Falha fechada: sem claim válida, lança exceção
/// em vez de devolver um tenant vazio que poderia vazar dados entre clínicas.
/// </summary>
internal sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid TenantId =>
        ClaimsPrincipalExtensions.TryGetTenantId(accessor.HttpContext?.User, out var tenantId)
            ? tenantId
            : throw new InvalidOperationException("A requisição não tem uma clínica (tenant) válida.");
}
