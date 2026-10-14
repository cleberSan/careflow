using System.Security.Claims;

namespace CareFlow.Scheduling.Api.Common.Security;

internal static class ClaimsPrincipalExtensions
{
    public const string TenantClaim = "tenant_id";

    private static readonly string[] ScopeClaims = ["scope", "scp"];

    public static bool TryGetTenantId(ClaimsPrincipal? user, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        var value = user?.FindFirst(TenantClaim)?.Value;
        return Guid.TryParse(value, out tenantId) && tenantId != Guid.Empty;
    }

    /// <summary>
    /// Aceita escopos como várias claims ou como uma única claim separada por espaços (RFC 8693).
    /// </summary>
    public static bool HasAnyScope(this ClaimsPrincipal user, params ReadOnlySpan<string> scopes)
    {
        foreach (var claim in user.Claims)
        {
            if (!ScopeClaims.Contains(claim.Type, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var granted in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var scope in scopes)
                {
                    if (string.Equals(granted, scope, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
