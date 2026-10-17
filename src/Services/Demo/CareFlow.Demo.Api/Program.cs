using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

using CareFlow.BuildingBlocks.AspNetCore.Hosting;
using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.Demo.Api.Common;
using CareFlow.Demo.Api.Features;
using CareFlow.Demo.Api.Services;
using CareFlow.Demo.Api.Sessions;
using CareFlow.Demo.Api.Telemetry;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

// Healthcheck do container (ADR-0013): sai antes de montar o host.
if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args).ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);

// O Demo existe para o Painel e usa um emissor de tokens próprio: roda no desenvolvimento, nos testes e na
// demonstração hospedada (ambiente Demo, ADR-0013), nunca num ambiente de produção com dados reais.
if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException("O serviço Demo não roda em produção; a demonstração hospedada usa o ambiente Demo.");
}

// Interruptor (ADR-0013): um arquivo fora da imagem, relido a cada mudança, liga e desliga o Demo sem deploy.
if (builder.Configuration["Demo:SwitchFile"] is { Length: > 0 } switchFile)
{
    builder.Configuration.AddJsonFile(switchFile, optional: true, reloadOnChange: true);
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 4 * 1024;
});

builder.AddCareFlowObservability("careflow-demo");
builder.Services.Replace(ServiceDescriptor.Singleton<DistributedContextPropagator>(new InboundIgnoringPropagator(TraceContextOnlyPropagator.Instance)));

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<DemoOptions>()
    .BindConfiguration(DemoOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => o.Services.Vault is not null && o.Services.Consent is not null && o.Services.Scheduling is not null
                   && o.Services.Notifications is not null && o.Services.Audit is not null && o.Services.Triage is not null
                   && o.Services.Rights is not null, "Demo:Services precisa dos sete endereços.")
    .Validate(o => o.FirstSlot + o.Slots - 1 <= 64, "Demo:FirstSlot + Demo:Slots passa da partição 64.")
    .ValidateOnStart();
builder.Services.AddOptions<DemoSwitchOptions>().BindConfiguration(DemoSwitchOptions.SectionName);
builder.Services.AddOptions<DemoTokenOptions>()
    .BindConfiguration(DemoTokenOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<DemoTokenIssuer>();
builder.Services.TryAddSingleton<IServiceAuthenticator>(provider => provider.GetRequiredService<DemoTokenIssuer>());
builder.Services.AddTransient<ServiceAuthenticationHandler>();
builder.Services.AddSingleton<CareFlowServices>();
builder.Services.AddSingleton<DemoSessions>();
builder.Services.AddSingleton<IClinicLedger>(provider =>
    provider.GetRequiredService<IOptions<DemoOptions>>().Value.LedgerPath is { Length: > 0 } ledgerPath
        ? new FileClinicLedger(ledgerPath)
        : new InMemoryClinicLedger());
builder.Services.AddSingleton<SessionLifecycle>();
builder.Services.AddHostedService<SessionCleaner>();

// Endereços lidos uma vez na subida (a validação acima recusa a subida sem eles).
var demo = builder.Configuration.GetSection(DemoOptions.SectionName).Get<DemoOptions>() ?? new DemoOptions();

// CORS só para as origens do site, sem credenciais: a sessão vai no cabeçalho Authorization, não em cookie.
builder.Services.AddCors(cors => cors.AddPolicy(DemoHttp.CorsPolicy, policy => policy
    .WithOrigins([.. demo.AllowedOrigins])
    .WithMethods("GET", "POST", "PUT", "DELETE")
    .WithHeaders("Authorization", "Content-Type", DemoHttp.CsrfHeader)
    .WithExposedHeaders("Retry-After")
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

// Atrás do proxy, o endereço do visitante vem do X-Forwarded-For, mas só de quem está nas redes do proxy e só um
// salto: um X-Forwarded-For forjado pelo navegador não muda a partição dos limites.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    forwarded.ForwardLimit = 1;
    forwarded.KnownProxies.Clear();
    forwarded.KnownIPNetworks.Clear();
    foreach (var network in demo.Proxy.KnownNetworks)
    {
        forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});
foreach (var service in CareFlowService.All)
{
    var address = service.Name switch
    {
        "vault" => demo.Services.Vault,
        "consent" => demo.Services.Consent,
        "scheduling" => demo.Services.Scheduling,
        "notifications" => demo.Services.Notifications,
        "triage" => demo.Services.Triage,
        "rights" => demo.Services.Rights,
        _ => demo.Services.Audit,
    };
    builder.Services.AddHttpClient(service.Name, client => client.BaseAddress = address)
        .AddHttpMessageHandler<ServiceAuthenticationHandler>()
        .AddStandardResilienceHandler(options => options.Retry.DisableForUnsafeHttpMethods());
}

if (demo.TempoBaseAddress is not null && demo.LokiBaseAddress is not null)
{
    builder.Services.AddHttpClient(GrafanaStackReader.TempoClient, client => client.BaseAddress = demo.TempoBaseAddress)
        .AddStandardResilienceHandler();
    builder.Services.AddHttpClient(GrafanaStackReader.LokiClient, client => client.BaseAddress = demo.LokiBaseAddress)
        .AddStandardResilienceHandler();
    builder.Services.TryAddSingleton<ITelemetryReader, GrafanaStackReader>();
}
else
{
    builder.Services.TryAddSingleton<ITelemetryReader, UnavailableTelemetryReader>();
}

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };

    // Teto por endereço de origem em todas as rotas do Painel, somando as sessões dele: um script com várias
    // sessões não passa do teto do endereço. A rota de saúde fica de fora (só a rede interna chega nela).
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!context.Request.Path.StartsWithSegments("/demo", StringComparison.Ordinal))
        {
            return RateLimitPartition.GetNoLimiter("interno");
        }

        var limits = context.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value.RateLimits;
        return RateLimitPartition.GetTokenBucketLimiter(DemoHttp.ClientPartition(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = limits.RequestsPerMinutePerAddress,
            TokensPerPeriod = limits.RequestsPerMinutePerAddress,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });

    // Sessões novas por endereço de origem: quem abre muitas sessões esgota o pool dos outros visitantes.
    limiter.AddPolicy(DemoHttp.SessionCreationPolicy, context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value.RateLimits;
        return RateLimitPartition.GetFixedWindowLimiter(DemoHttp.ClientPartition(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.SessionsPerWindow,
            Window = limits.SessionWindow,
            QueueLimit = 0,
        });
    });

    // Ações por sessão (cada sessão é uma clínica): balde de fichas, para cliques normais passarem e scripts não.
    limiter.AddPolicy(DemoHttp.SessionPolicy, context =>
    {
        var limits = context.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value.RateLimits;
        return RateLimitPartition.GetTokenBucketLimiter(DemoHttp.SessionPartition(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = limits.RequestsPerMinute,
            TokensPerPeriod = limits.RequestsPerMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

builder.Services.AddHealthChecks();

var app = builder.Build();

if (demo.Proxy.KnownNetworks.Count > 0)
{
    app.UseForwardedHeaders();
}

// Falha de um serviço vira 502 sem detalhes: endereços e mensagens internas não chegam ao navegador.
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = exception is UpstreamException ? StatusCodes.Status502BadGateway : StatusCodes.Status500InternalServerError;
    await Results.Problem(
            statusCode: context.Response.StatusCode,
            title: exception is UpstreamException ? "Um serviço do CareFlow não respondeu. Tente de novo." : "Erro inesperado.")
        .ExecuteAsync(context)
        .ConfigureAwait(false);
}));

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.CacheControl = "no-store";
    headers["Referrer-Policy"] = "no-referrer";
    await next(context).ConfigureAwait(false);
});

app.UseCors(DemoHttp.CorsPolicy);
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapDemoEndpoints();

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>Ponto de entrada; público para os testes com WebApplicationFactory.</summary>
public partial class Program;
