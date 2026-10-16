using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.Demo.Api.Common;
using CareFlow.Demo.Api.Features;
using CareFlow.Demo.Api.Services;
using CareFlow.Demo.Api.Sessions;
using CareFlow.Demo.Api.Telemetry;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// O Demo existe para o Painel e usa um emissor de tokens local: não sobe em produção (ADR-0010).
if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException("O serviço Demo não roda em produção: hospedar o Painel ao vivo é decisão da fase 9.");
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
builder.Services.AddOptions<DemoTokenOptions>()
    .BindConfiguration(DemoTokenOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<DemoTokenIssuer>();
builder.Services.TryAddSingleton<IServiceAuthenticator>(provider => provider.GetRequiredService<DemoTokenIssuer>());
builder.Services.AddTransient<ServiceAuthenticationHandler>();
builder.Services.AddSingleton<CareFlowServices>();
builder.Services.AddSingleton<DemoSessions>();
builder.Services.AddSingleton<SessionLifecycle>();
builder.Services.AddHostedService<SessionCleaner>();

// Endereços lidos uma vez na subida (a validação acima recusa a subida sem eles).
var demo = builder.Configuration.GetSection(DemoOptions.SectionName).Get<DemoOptions>() ?? new DemoOptions();
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

    // Ações por sessão: balde de fichas, para cliques normais passarem e scripts não.
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

app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapDemoEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Ponto de entrada; público para os testes com WebApplicationFactory.</summary>
public partial class Program;
