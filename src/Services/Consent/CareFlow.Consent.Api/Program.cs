using System.Text.Json.Serialization;

using CareFlow.BuildingBlocks.AspNetCore.Hosting;
using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.BuildingBlocks.AspNetCore.Tenancy;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.BuildingBlocks.Persistence.Demo;
using CareFlow.Consent.Api.Common.Observability;
using CareFlow.Consent.Api.Common.Security;
using CareFlow.Consent.Api.Features.Consents;
using CareFlow.Consent.Api.Features.Rights;
using CareFlow.Consent.Api.Infrastructure.Persistence;
using CareFlow.Contracts.Rights;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// Healthcheck do container (ADR-0013): sai antes de montar o host.
if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args).ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.AddCareFlowObservability("careflow-consent", ConsentMetrics.MeterName);
builder.Services.AddSingleton<ConsentMetrics>();

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// Configuração do emissor em Authentication:Schemes:Bearer (Authority, ValidAudiences).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false);
builder.Services.AddAuthorizationBuilder().AddConsentPolicies();

builder.Services.AddHttpTenantContext();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddConsentDatabase(builder.Configuration);

// O Consent publica ConsentGranted/ConsentRevoked e os acessos para a auditoria, e responde aos pedidos do titular.
builder.Services.AddScoped<ConsentEraser>();
builder.Services
    .AddCareFlowMessaging(builder.Configuration, DatabaseExtensions.RelayConnectionStringName)
    .AddOutbox()
    .AddConsumer<ConsentDbContext>(consumers => consumers
        .Handle<SubjectRequestOpened, SubjectRequestOpenedConsumer>()
        .Handle<ErasureReapplied, ErasureReappliedConsumer>());

builder.Services.AddConsentFeatures();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapConsentEndpoints();

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
