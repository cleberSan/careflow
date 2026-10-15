using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CareFlow.Notifications.Api.Contacts;

/// <summary>Emissor interno local de tokens de serviço. Chave privada P-256 (PKCS#8 em Base64) de user-secrets ou do ambiente.</summary>
internal sealed class ServiceTokenOptions
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

/// <summary>
/// Token curto, por clínica, só com o escopo de leitura de contato e só para o cofre. O cofre valida com a chave
/// pública e aceita esse token apenas no endpoint de contato. Recusado em produção (client credentials do IdP na
/// fase 9). Cada clínica tem o seu token em cache até um minuto antes de expirar.
/// </summary>
internal sealed class ServiceTokenIssuer : IDisposable
{
    public const string Subject = "careflow-notifications";
    public const string VaultAudience = "careflow-patient-vault";
    public const string ContactReadScope = "vault.contact.read";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly ECDsa _key;
    private readonly SigningCredentials _credentials;
    private readonly ServiceTokenOptions _options;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly ConcurrentDictionary<Guid, (string Token, DateTimeOffset RefreshAt)> _cache = new();

    public ServiceTokenIssuer(IOptions<ServiceTokenOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _time = time;
        _key = LoadPrivateKey(_options.PrivateKey);

        // Sem cache global de provedores de assinatura: o cache do IdentityModel é indexado pela chave pública e
        // poderia devolver o ECDsa de outra instância já descartada. Assinar é raro (o token fica em cache).
        var securityKey = new ECDsaSecurityKey(_key) { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } };
        _credentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256);
    }

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
            Audience = VaultAudience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Subject,
                ["tenant_id"] = tenantId.ToString(),
                ["scope"] = ContactReadScope,
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
