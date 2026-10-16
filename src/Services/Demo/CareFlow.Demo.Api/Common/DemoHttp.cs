using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Demo.Api.Sessions;

using Microsoft.Extensions.Options;

namespace CareFlow.Demo.Api.Common;

/// <summary>Token de sessão e as regras que toda rota do Painel aplica antes do handler.</summary>
/// <remarks>
/// A sessão é um token opaco de 32 bytes aleatórios no cabeçalho <c>Authorization: Bearer</c>, guardado só na memória
/// da página. Um cookie não serviria: o site (vercel.app) e o backend (duckdns.org) são sites diferentes, e
/// navegadores bloqueiam cookie de terceiro (ADR-0013). Como nada vai sozinho em requisição de outro site, não há
/// CSRF; o cabeçalho <c>X-CareFlow-Demo</c> e a lista de origens continuam como segunda barreira.
/// </remarks>
internal static class DemoHttp
{
    public const string AuthorizationScheme = "Bearer";

    /// <summary>Cabeçalho obrigatório: força o preflight de CORS, que só as origens do site passam.</summary>
    public const string CsrfHeader = "X-CareFlow-Demo";

    public const string CorsPolicy = "careflow-site";

    public const string SessionCreationPolicy = "demo-session-creation";
    public const string SessionPolicy = "demo-session";

    /// <summary>Comprimento do token (32 bytes em Base64url, sem preenchimento).</summary>
    public const int TokenLength = 43;

    private const string SessionItem = "careflow.demo.session";

    public static readonly Error MissingHeader = Error.Forbidden("Demo.CabecalhoAusente", $"Envie o cabeçalho {CsrfHeader}: 1.");

    public static readonly Error ForeignOrigin = Error.Forbidden("Demo.OrigemRecusada", "Origem não autorizada.");

    public static readonly Error NoSession = Error.Unauthorized("Demo.SemSessao", "Sessão de demonstração ausente ou vencida. Comece uma nova.");

    public static readonly Error SwitchedOff = Error.Failure("Demo.Desligado", "A demonstração ao vivo está desligada agora. O Painel mostra a gravação.");

    public static DemoSession Session(this HttpContext context) =>
        context.Items[SessionItem] as DemoSession ?? throw new InvalidOperationException("Rota sem o filtro de sessão.");

    /// <summary>O token do cabeçalho, ou nada se faltar ou tiver formato inesperado.</summary>
    public static string? Token(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var prefix = AuthorizationScheme + " ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[prefix.Length..].Trim();
        return token.Length == TokenLength ? token : null;
    }

    /// <summary>Partição do limite por sessão: hash do token (o valor em si não fica como chave em memória).</summary>
    public static string SessionPartition(HttpContext context) =>
        Token(context) is { } token ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) : "sem-sessao";

    /// <summary>
    /// Partição dos limites por origem: o endereço do visitante, nunca registrado em log. Atrás do proxy, é o que o
    /// middleware de cabeçalhos encaminhados já validou (só um salto, só das redes do proxy).
    /// </summary>
    public static string ClientPartition(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

    /// <summary>Resposta de serviço indisponível com <c>Retry-After</c>, para o site cair no replay e tentar depois.</summary>
    public static IResult Unavailable(HttpContext context, Error error, int retryAfterSeconds)
    {
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    /// <summary>Interruptor, cabeçalho anti-CSRF e origem permitida, em toda rota do Painel.</summary>
    public sealed class RequestFilter(IOptions<DemoOptions> options, IOptionsMonitor<DemoSwitchOptions> demoSwitch) : IEndpointFilter
    {
        public const int SwitchedOffRetryAfterSeconds = 300;

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            var request = context.HttpContext.Request;
            if (!string.Equals(request.Headers[CsrfHeader], "1", StringComparison.Ordinal))
            {
                return MissingHeader.ToProblem();
            }

            var origin = request.Headers.Origin.ToString();
            if (origin.Length > 0 && !options.Value.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                return ForeignOrigin.ToProblem();
            }

            if (!demoSwitch.CurrentValue.Enabled && context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AvailableWhenSwitchedOff>() is null)
            {
                return Unavailable(context.HttpContext, SwitchedOff, SwitchedOffRetryAfterSeconds);
            }

            return await next(context).ConfigureAwait(false);
        }
    }

    /// <summary>Marca a rota que continua respondendo com o Demo desligado (a de estado, que o site consulta).</summary>
    public sealed class AvailableWhenSwitchedOff;

    /// <summary>Resolve a sessão pelo token; sem sessão válida, 401.</summary>
    public sealed class SessionFilter(DemoSessions sessions) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            if (sessions.Find(Token(context.HttpContext)) is not { } session)
            {
                return NoSession.ToProblem();
            }

            context.HttpContext.Items[SessionItem] = session;
            return await next(context).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// O Demo não aceita contexto de trace de quem chama: cada ação do visitante começa um trace novo, que a sessão
/// passa a ser dona. Um <c>traceparent</c> escolhido pelo navegador não liga a sessão ao trace de outra. Para
/// fora (chamadas aos serviços), propaga só <c>traceparent</c>, como os demais.
/// </summary>
internal sealed class InboundIgnoringPropagator(DistributedContextPropagator outbound) : DistributedContextPropagator
{
    public override IReadOnlyCollection<string> Fields => outbound.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        outbound.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}
