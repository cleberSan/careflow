using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.BuildingBlocks.AspNetCore.Tenancy;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.BuildingBlocks.Persistence.Demo;
using CareFlow.PatientVault.Api.Common.Observability;
using CareFlow.PatientVault.Api.Common.Security;
using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Cryptography.KeyManagement;
using CareFlow.PatientVault.Api.Features.Patients;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.AddCareFlowObservability("careflow-patient-vault", VaultMetrics.MeterName);
builder.Services.AddSingleton<VaultMetrics>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Configuração do emissor em Authentication:Schemes:Bearer (Authority, ValidAudiences).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false)
    .AddInternalServiceTokens(builder.Configuration, builder.Environment);
builder.Services.AddAuthorizationBuilder().AddVaultPolicies();

builder.Services.AddHttpTenantContext();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddKeyManagement(builder.Configuration, builder.Environment);
builder.Services.AddMemoryCache();
builder.Services.AddOptions<KeyCacheOptions>()
    .BindConfiguration(KeyCacheOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<TenantKeyRing>();

builder.Services.AddVaultDatabase(builder.Configuration);

builder.Services.AddPatientFeatures();

// O cofre só publica: cadastro de paciente para a agenda e acessos para a auditoria.
builder.Services
    .AddCareFlowMessaging(builder.Configuration, DatabaseExtensions.RelayConnectionStringName)
    .AddOutbox();

var app = builder.Build();

app.Services.ValidateKeyManagementOnStart();

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

app.MapPatientEndpoints();

app.MapDemoPurgeIfEnabled(DatabaseExtensions.ConnectionStringName, AuthorizationPolicies.DemoPurge);

await app.MigrateIfEnabledAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

/// <summary>Ponto de entrada; público para os testes de integração com WebApplicationFactory.</summary>
public partial class Program;
