using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CareFlow.BuildingBlocks.AspNetCore.Security.ServiceTokens;

/// <summary>Emissor interno local de tokens de serviço. Chave privada P-256 (PKCS#8 em Base64) de user-secrets ou do ambiente.</summary>
public sealed class ServiceTokenOptions
{
    public const string SectionName = "ServiceTokens:Local";
    public const string DefaultIssuer = "careflow-internal-local";

    [Required]
    public string PrivateKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = DefaultIssuer;

    [Range(typeof(TimeSpan), "00:01:00", "00:15:00")]
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Quem pede (<see cref="Subject"/>), para qual serviço (<see cref="Audience"/>) e com qual escopo, um só.</summary>
public sealed record ServiceTokenGrant(string Subject, string Audience, string Scope);

/// <summary>
/// Token curto, por clínica, com um único escopo e uma única audiência. O serviço de destino valida com a
/// chave pública e aceita esse token só na rota daquele escopo. Recusado em produção (client credentials do
/// IdP na fase 9). Cada clínica tem o seu token em cache até um minuto antes de expirar.
/// </summary>
public sealed class ServiceTokenIssuer : IDisposable
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly ECDsa _key;
    private readonly SigningCredentials _credentials;
    private readonly ServiceTokenOptions _options;
    private readonly ServiceTokenGrant _grant;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly ConcurrentDictionary<Guid, (string Token, DateTimeOffset RefreshAt)> _cache = new();

    public ServiceTokenIssuer(IOptions<ServiceTokenOptions> options, ServiceTokenGrant grant, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.Audience);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.Scope);
        _options = options.Value;
        _grant = grant;
        _time = time;
        _key = LoadPrivateKey(_options.PrivateKey);

        // Sem cache global de provedores de assinatura: o cache do IdentityModel é indexado pela chave pública e
        // poderia devolver o ECDsa de outra instância já descartada. Assinar é raro (o token fica em cache).
        var securityKey = new ECDsaSecurityKey(_key) { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } };
        _credentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256);
    }

    public ServiceTokenGrant Grant => _grant;

    public string TokenFor(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Clínica obrigatória no token de serviço.", nameof(tenantId));
        }

        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(tenantId, out var cached) && now < cached.RefreshAt)
        {
            return cached.Token;
        }

        var expires = now + _options.Lifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _grant.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = _grant.Subject,
                ["tenant_id"] = tenantId.ToString(),
                ["scope"] = _grant.Scope,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = _credentials,
        });

        _cache[tenantId] = (token, expires - RefreshMargin);
        return token;
    }

    public void Dispose() => _key.Dispose();

    private static ECDsa LoadPrivateKey(string base64)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
            if (ecdsa.KeySize == 256)
            {
                return ecdsa;
            }
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            ecdsa.Dispose();
            throw new InvalidOperationException($"{ServiceTokenOptions.SectionName}:PrivateKey inválida (esperado PKCS#8 P-256 em Base64).", exception);
        }

        ecdsa.Dispose();
        throw new InvalidOperationException($"{ServiceTokenOptions.SectionName}:PrivateKey precisa ser uma chave P-256.");
    }
}

public static class ServiceTokenServiceCollectionExtensions
{
    /// <summary>Registra o emissor local com a configuração de <see cref="ServiceTokenOptions.SectionName"/>. Recusado em produção.</summary>
    public static IServiceCollection AddLocalServiceTokens(this IServiceCollection services, IHostEnvironment environment, ServiceTokenGrant grant)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(grant);
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "O emissor interno local de tokens de serviço não pode ser usado em produção. Use client credentials do IdP.");
        }

        services.AddOptions<ServiceTokenOptions>()
            .BindConfiguration(ServiceTokenOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(provider => new ServiceTokenIssuer(
            provider.GetRequiredService<IOptions<ServiceTokenOptions>>(),
            grant,
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
