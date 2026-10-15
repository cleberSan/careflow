using System.Text.Json.Serialization;

using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.BuildingBlocks.AspNetCore.Tenancy;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.BuildingBlocks.Persistence.Demo;
using CareFlow.Contracts.Consents;
using CareFlow.Contracts.Patients;
using CareFlow.Scheduling.Api.Common.Observability;
using CareFlow.Scheduling.Api.Common.Security;
using CareFlow.Scheduling.Api.Features.Appointments;
using CareFlow.Scheduling.Api.Features.Patients;
using CareFlow.Scheduling.Api.Infrastructure;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.AddCareFlowObservability("careflow-scheduling", SchedulingMetrics.MeterName);
builder.Services.AddSingleton<SchedulingMetrics>();

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// Configuração do emissor em Authentication:Schemes:Bearer (Authority, ValidAudiences).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false);
builder.Services.AddAuthorizationBuilder().AddSchedulingPolicies();

builder.Services.AddHttpTenantContext();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<SchedulingOptions>()
    .BindConfiguration(SchedulingOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(
        options => options.MinimumDurationMinutes <= options.MaximumDurationMinutes,
        "Scheduling:MinimumDurationMinutes não pode ser maior que MaximumDurationMinutes.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SchedulingOptions>>().Value.ToPolicy());

builder.Services.AddSchedulingDatabase(builder.Configuration);

builder.Services.AddAppointmentFeatures();

// Publica auditoria pelo Outbox e mantém a projeção de pacientes com os eventos do cofre e do Consent.
builder.Services
    .AddCareFlowMessaging(builder.Configuration, DatabaseExtensions.RelayConnectionStringName)
    .AddOutbox()
    .AddConsumer<SchedulingDbContext>(consumers => consumers
        .Handle<PatientRegistered, PatientRegisteredConsumer>()
        .Handle<ConsentGranted, ConsentGrantedConsumer>()
        .Handle<ConsentRevoked, ConsentRevokedConsumer>());

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

app.MapAppointmentEndpoints();

app.MapDemoPurgeIfEnabled(DatabaseExtensions.ConnectionStringName, AuthorizationPolicies.DemoPurge);

await app.MigrateIfEnabledAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

/// <summary>Ponto de entrada; público para os testes de integração com WebApplicationFactory.</summary>
public partial class Program;
