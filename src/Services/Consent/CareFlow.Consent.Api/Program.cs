using System.Text.Json.Serialization;

using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.BuildingBlocks.Persistence.Demo;
using CareFlow.BuildingBlocks.AspNetCore.Tenancy;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Consent.Api.Common.Observability;
using CareFlow.Consent.Api.Common.Security;
using CareFlow.Consent.Api.Features.Consents;
using CareFlow.Consent.Api.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

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

// O Consent só publica: ConsentGranted/ConsentRevoked e os acessos para a auditoria.
builder.Services
    .AddCareFlowMessaging(builder.Configuration, DatabaseExtensions.RelayConnectionStringName)
    .AddOutbox();

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

await app.MigrateIfEnabledAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

/// <summary>Ponto de entrada; público para os testes de integração com WebApplicationFactory.</summary>
public partial class Program;
