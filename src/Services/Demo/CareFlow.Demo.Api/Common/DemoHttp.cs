using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Demo.Api.Sessions;

using Microsoft.Extensions.Options;

namespace CareFlow.Demo.Api.Common;

/// <summary>Cookie de sessão e as regras que toda rota do Painel aplica antes do handler.</summary>
internal static class DemoHttp
{
    /// <summary>
    /// <c>__Host-</c>: o navegador só aceita com Secure, Path=/ e sem Domain, então nem um subdomínio consegue
    /// sobrescrever. HttpOnly (script não lê) e SameSite=Strict (não vai em requisição de outro site).
    /// </summary>
    public const string CookieName = "__Host-careflow-demo";

    /// <summary>Cabeçalho obrigatório: um formulário de outro site não consegue enviá-lo sem CORS, que o Demo não libera.</summary>
    public const string CsrfHeader = "X-CareFlow-Demo";

    public const string SessionCreationPolicy = "demo-session-creation";
    public const string SessionPolicy = "demo-session";

    private const string SessionItem = "careflow.demo.session";

    public static readonly Error MissingHeader = Error.Forbidden("Demo.CabecalhoAusente", $"Envie o cabeçalho {CsrfHeader}: 1.");

    public static readonly Error ForeignOrigin = Error.Forbidden("Demo.OrigemRecusada", "Origem não autorizada.");

    public static readonly Error NoSession = Error.Unauthorized("Demo.SemSessao", "Sessão de demonstração ausente ou vencida. Comece uma nova.");

    public static CookieOptions CookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };

    public static DemoSession Session(this HttpContext context) =>
        context.Items[SessionItem] as DemoSession ?? throw new InvalidOperationException("Rota sem o filtro de sessão.");

    /// <summary>Partição do limite por sessão: hash do cookie (o valor em si não fica como chave em memória).</summary>
    public static string SessionPartition(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var cookie) && cookie.Length is > 0 and <= 64
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)))
            : "sem-sessao";

    /// <summary>Partição do limite de criação: o endereço de origem, nunca registrado em log.</summary>
    public static string ClientPartition(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

    /// <summary>Cabeçalho anti-CSRF e origem permitida, em toda rota do Painel.</summary>
    public sealed class RequestFilter(IOptions<DemoOptions> options) : IEndpointFilter
    {
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

            return await next(context).ConfigureAwait(false);
        }
    }

    /// <summary>Resolve a sessão pelo cookie; sem sessão válida, 401.</summary>
    public sealed class SessionFilter(DemoSessions sessions) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            context.HttpContext.Request.Cookies.TryGetValue(CookieName, out var cookie);
            if (sessions.Find(cookie) is not { } session)
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
