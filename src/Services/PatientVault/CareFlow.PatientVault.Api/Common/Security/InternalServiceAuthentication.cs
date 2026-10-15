using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace CareFlow.PatientVault.Api.Common.Security;

/// <summary>
/// Tokens de serviço emitidos pelo emissor interno local (ES256, curtos e por clínica). O cofre só guarda a
/// chave pública: quem valida não consegue emitir. Esse esquema vale apenas nas políticas que o citam
/// (hoje, a leitura de contato); as demais rotas autenticam só pelo esquema padrão.
/// Recusado em produção: lá o token vem do IdP por client credentials (fase 9).
/// </summary>
internal static class InternalServiceAuthentication
{
    public const string SchemeName = "Internal";
    public const string SectionName = "InternalAuth";
    public const string DefaultIssuer = "careflow-internal-local";
    public const string Audience = "careflow-patient-vault";

    public static AuthenticationBuilder AddInternalServiceTokens(
        this AuthenticationBuilder builder,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var section = configuration.GetSection(SectionName);
        var publicKey = section["PublicKey"];
        if (!string.IsNullOrWhiteSpace(publicKey) && environment.IsProduction())
        {
            throw new InvalidOperationException(
                "O emissor interno local de tokens de serviço não pode ser usado em produção. Use client credentials do IdP.");
        }

        var issuer = section["Issuer"] is { Length: > 0 } configured ? configured : DefaultIssuer;

        // Carregada já no registro: chave inválida derruba a subida, não a primeira chamada.
        var signingKey = string.IsNullOrWhiteSpace(publicKey) ? null : LoadPublicKey(publicKey);
        return builder.AddJwtBearer(SchemeName, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidAudience = Audience,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                // Sem chave configurada o esquema existe, mas não valida nenhum token (falha fechada).
                IssuerSigningKey = signingKey,
            };
        });
    }

    private static ECDsaSecurityKey LoadPublicKey(string base64)
    {
        try
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);
            if (ecdsa.KeySize != 256)
            {
                ecdsa.Dispose();
                throw new InvalidOperationException($"{SectionName}:PublicKey precisa ser uma chave P-256.");
            }

            return new ECDsaSecurityKey(ecdsa);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidOperationException($"{SectionName}:PublicKey inválida (esperado SubjectPublicKeyInfo em Base64).", exception);
        }
    }
}
