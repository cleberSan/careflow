using System.Threading.RateLimiting;

using CareFlow.BuildingBlocks.AspNetCore.Hosting;
using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.BuildingBlocks.Persistence.Demo;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Common.Cryptography;
using CareFlow.Rights.Api.Common.Observability;
using CareFlow.Rights.Api.Common.Security;
using CareFlow.Rights.Api.Features;
using CareFlow.Rights.Api.Features.Exports;
using CareFlow.Rights.Api.Features.Messaging;
using CareFlow.Rights.Api.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// Healthcheck do container (ADR-0013): sai antes de montar o host.
if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args).ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.AddCareFlowObservability("careflow-rights", RightsMetrics.MeterName);
builder.Services.AddSingleton<RightsMetrics>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Configuração do emissor em Authentication:Schemes:Bearer (Authority, ValidAudiences).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false);
builder.Services.AddAuthorizationBuilder().AddRightsPolicies();

// O download é anônimo (o titular não tem conta): limite por endereço para quem tenta adivinhar tokens.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(RightsEndpoints.DownloadRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddRightsTenantContext();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<RightsKeyOptions>()
    .BindConfiguration(RightsKeyOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<IdentityVerificationOptions>()
    .BindConfiguration(IdentityVerificationOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<RightsKeyRing>();

builder.Services.AddRightsDatabase(builder.Configuration);
builder.Services.AddRightsFeatures();
if (builder.Configuration.GetValue("Rights:SweepPackages", true))
{
    builder.Services.AddHostedService<PackageSweeperService>();
}

// Publica o pedido para os serviços e o código para o Notifications; recebe as respostas.
builder.Services
    .AddCareFlowMessaging(builder.Configuration, DatabaseExtensions.RelayConnectionStringName)
    .AddOutbox()
    .AddConsumer<RightsDbContext>(consumers => consumers
        .Handle<IdentityVerificationDispatched, IdentityVerificationDispatchedConsumer>()
        .Handle<SubjectRequestAnswered, SubjectRequestAnsweredConsumer>());

var app = builder.Build();

// Chave do serviço ou chave pública do Notifications inválidas derrubam o processo na subida.
_ = app.Services.GetRequiredService<RightsKeyRing>();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapRightsEndpoints();

app.MapDemoPurgeIfEnabled(DatabaseExtensions.ConnectionStringName, AuthorizationPolicies.DemoPurge);

if (await app.MigrateIfEnabledAsync().ConfigureAwait(false))
{
    // Etapa de migrations do deploy: o container termina aqui, sem subir a API.
    return 0;
}

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>Ponto de entrada; público para os testes de integração com WebApplicationFactory.</summary>
public partial class Program;
