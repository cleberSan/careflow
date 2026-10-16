using CareFlow.Audit.Api.Chain;
using CareFlow.Audit.Api.Common.Observability;
using CareFlow.Audit.Api.Common.Security;
using CareFlow.Audit.Api.Features;
using CareFlow.Audit.Api.Features.Messaging;
using CareFlow.Audit.Api.Features.Rights;
using CareFlow.Audit.Api.Infrastructure.Persistence;
using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.BuildingBlocks.AspNetCore.Tenancy;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Contracts.Auditing;
using CareFlow.Contracts.Rights;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.AddCareFlowObservability("careflow-audit", AuditMetrics.MeterName);
builder.Services.AddSingleton<AuditMetrics>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Configuração do emissor em Authentication:Schemes:Bearer (Authority, ValidAudiences).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false);
builder.Services.AddAuthorizationBuilder().AddAuditPolicies();

builder.Services.AddHttpTenantContext();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<ChainKeyOptions>()
    .BindConfiguration(ChainKeyOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<ChainKeyRing>();

builder.Services.AddAuditDatabase(builder.Configuration);

builder.Services.AddAuditFeatures();

// A auditoria grava na cadeia os acessos que os outros serviços publicam e responde aos pedidos do titular
// (o único evento que ela publica, pelo Outbox).
builder.Services
    .AddCareFlowMessaging(builder.Configuration, DatabaseExtensions.RelayConnectionStringName)
    .AddOutbox()
    .AddConsumer<AuditDbContext>(consumers => consumers
        .Handle<DataAccessRecorded, DataAccessRecordedConsumer>()
        .Handle<ContentDisclosed, ContentDisclosedConsumer>()
        .Handle<SubjectRequestOpened, SubjectRequestOpenedConsumer>());

var app = builder.Build();

// Chave ausente ou inválida derruba o processo na subida, não na primeira gravação.
_ = app.Services.GetRequiredService<ChainKeyRing>();

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

app.MapAuditEndpoints();

await app.MigrateIfEnabledAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

/// <summary>Ponto de entrada; público para os testes de integração com WebApplicationFactory.</summary>
public partial class Program;
