using System.Text.Json.Serialization;

using CareFlow.BuildingBlocks.AspNetCore.Hosting;
using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.BuildingBlocks.AspNetCore.Tenancy;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.BuildingBlocks.Persistence.Demo;
using CareFlow.Contracts.Consents;
using CareFlow.Contracts.Rights;
using CareFlow.Triage.Api.Common.Observability;
using CareFlow.Triage.Api.Common.Security;
using CareFlow.Triage.Api.Features.Consents;
using CareFlow.Triage.Api.Features.Rights;
using CareFlow.Triage.Api.Features.Triages;
using CareFlow.Triage.Api.Infrastructure.Persistence;
using CareFlow.Triage.Api.Models;
using CareFlow.Triage.Api.Profiles;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// Healthcheck do container (ADR-0013): sai antes de montar o host.
if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args).ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);

// Queixa de até 1000 caracteres: 16 KiB cobre o JSON com folga e corta corpos abusivos cedo.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.AddCareFlowObservability("careflow-triage", TriageMetrics.MeterName);
builder.Services.AddSingleton<TriageMetrics>();

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// Configuração do emissor em Authentication:Schemes:Bearer (Authority, ValidAudiences).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false);
builder.Services.AddAuthorizationBuilder().AddTriagePolicies();

builder.Services.AddHttpTenantContext();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddTriageDatabase(builder.Configuration);

// Consome o consentimento ai-triage do Consent; publica a auditoria pelo Outbox.
builder.Services
    .AddCareFlowMessaging(builder.Configuration, DatabaseExtensions.RelayConnectionStringName)
    .AddOutbox()
    .AddConsumer<TriageDbContext>(consumers => consumers
        .Handle<ConsentGranted, ConsentGrantedConsumer>()
        .Handle<ConsentRevoked, ConsentRevokedConsumer>()
        .Handle<SubjectRequestOpened, SubjectRequestOpenedConsumer>()
        .Handle<ErasureReapplied, ErasureReappliedConsumer>());
builder.Services.AddScoped<TriageEraser>();

builder.Services.AddTriageModel(builder.Configuration, builder.Environment);
builder.Services.AddTriageProfiles(builder.Environment);
builder.Services.AddTriageFeatures();

var app = builder.Build();

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

app.MapTriageEndpoints();

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
