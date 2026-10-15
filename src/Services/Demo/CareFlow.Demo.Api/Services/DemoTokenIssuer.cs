using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CareFlow.Demo.Api.Services;

/// <summary>
/// Emissor local de tokens do Demo (HS256). Os serviços aceitam esse emissor pela configuração padrão do
/// JwtBearer (<c>Authentication:Schemes:Bearer:SigningKeys</c>), como os tokens do <c>dotnet user-jwts</c>.
/// Recusado em produção: um IdP de verdade entra quando o Painel for hospedado (fase 9).
/// </summary>
internal sealed class DemoTokenOptions
{
    public const string SectionName = "Demo:Tokens";
    public const string DefaultIssuer = "careflow-demo-local";

    /// <summary>Chave simétrica em Base64, com pelo menos 32 bytes.</summary>
    [Required]
    public string SigningKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = DefaultIssuer;

    [Range(typeof(TimeSpan), "00:00:30", "00:10:00")]
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>Como o Demo se identifica para um serviço numa chamada: clínica da sessão, audiência e escopos.</summary>
internal interface IServiceAuthenticator
{
    void Authenticate(HttpRequestMessage request, ServiceCall call);

    /// <summary>Descarta o que estiver guardado para uma clínica que deixou de existir.</summary>
    void Forget(Guid clinic);
}

internal sealed record ServiceCall(Guid Clinic, string Audience, IReadOnlyList<string> Scopes)
{
    public static readonly HttpRequestOptionsKey<ServiceCall> Key = new("careflow.demo.service-call");
}

/// <summary>Token curto por clínica, audiência e escopos, em cache até 30 segundos antes de vencer.</summary>
internal sealed class DemoTokenIssuer : IServiceAuthenticator
{
    public const string Subject = "careflow-demo";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    private readonly SigningCredentials _credentials;
    private readonly DemoTokenOptions _options;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset RefreshAt)> _cache = new(StringComparer.Ordinal);

    public DemoTokenIssuer(IOptions<DemoTokenOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _time = time;
        _credentials = new SigningCredentials(new SymmetricSecurityKey(DecodeKey(_options.SigningKey)), SecurityAlgorithms.HmacSha256);
    }

    public void Authenticate(HttpRequestMessage request, ServiceCall call)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        request.Headers.Authorization = new("Bearer", TokenFor(call));
    }

    public string TokenFor(ServiceCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Clinic == Guid.Empty)
        {
            throw new ArgumentException("Clínica obrigatória no token do Demo.", nameof(call));
        }

        var scope = string.Join(' ', call.Scopes.Order(StringComparer.Ordinal));
        var key = $"{call.Clinic:N}|{call.Audience}|{scope}";
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && now < cached.RefreshAt)
        {
            return cached.Token;
        }

        var expires = now + _options.Lifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = call.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Subject,
                ["tenant_id"] = call.Clinic.ToString(),
                ["scope"] = scope,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = _credentials,
        });

        _cache[key] = (token, expires - RefreshMargin);
        return token;
    }

    /// <summary>Tokens de clínicas que não existem mais saem do cache.</summary>
    public void Forget(Guid clinic)
    {
        var prefix = clinic.ToString("N") + "|";
        foreach (var key in _cache.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            _cache.TryRemove(key, out _);
        }
    }

    internal static byte[] DecodeKey(string base64)
    {
        try
        {
            var key = Convert.FromBase64String(base64);
            return key.Length >= 32
                ? key
                : throw new InvalidOperationException($"{DemoTokenOptions.SectionName}:SigningKey precisa de pelo menos 32 bytes.");
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"{DemoTokenOptions.SectionName}:SigningKey inválida (esperado Base64).", exception);
        }
    }
}

/// <summary>Põe a identificação da chamada (definida em <see cref="ServiceCall.Key"/>) em cada requisição.</summary>
internal sealed class ServiceAuthenticationHandler(IServiceAuthenticator authenticator) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Options.TryGetValue(ServiceCall.Key, out var call))
        {
            throw new InvalidOperationException("Chamada a serviço sem clínica e escopos definidos.");
        }

        authenticator.Authenticate(request, call);
        return base.SendAsync(request, cancellationToken);
    }
}
