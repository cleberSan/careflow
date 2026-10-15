using System.Net.Http.Headers;
using System.Security.Cryptography;

using CareFlow.PatientVault.Api.Common.Security;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CareFlow.PatientVault.IntegrationTests.Infrastructure;

/// <summary>Faz o papel do emissor interno: tokens de serviço ES256 com a chave que o cofre da fixture conhece.</summary>
internal sealed class InternalTokens : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKeyBase64 => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public string Issue(
        Guid? tenantId,
        string scope = "vault.contact.read",
        string issuer = InternalServiceAuthentication.DefaultIssuer,
        string audience = InternalServiceAuthentication.Audience,
        TimeSpan? lifetime = null,
        ECDsa? signingKey = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["sub"] = "careflow-notifications", ["scope"] = scope };
        if (tenantId is not null)
        {
            claims["tenant_id"] = tenantId.Value.ToString();
        }

        var expires = now + (lifetime ?? TimeSpan.FromMinutes(5));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = expires < now ? expires.AddMinutes(-5) : now,
            NotBefore = expires < now ? expires.AddMinutes(-5) : now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(signingKey ?? _key), SecurityAlgorithms.EcdsaSha256),
        });
    }

    public static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public void Dispose() => _key.Dispose();
}
