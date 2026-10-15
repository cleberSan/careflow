using System.Text.Json;
using System.Text.Json.Serialization;

using CareFlow.BuildingBlocks.AspNetCore.Observability;
using CareFlow.BuildingBlocks.Persistence.Demo;
using CareFlow.BuildingBlocks.AspNetCore.Tenancy;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Contracts.Appointments;
using CareFlow.Contracts.Consents;
using CareFlow.Notifications.Api.Channels;
using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Api.Common.Observability;
using CareFlow.Notifications.Api.Common.Security;
using CareFlow.Notifications.Api.Contacts;
using CareFlow.Notifications.Api.Delivery;
using CareFlow.Notifications.Api.Features;
using CareFlow.Notifications.Api.Features.Appointments;
using CareFlow.Notifications.Api.Features.Consents;
using CareFlow.Notifications.Api.Infrastructure;
using CareFlow.Notifications.Api.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.AddCareFlowObservability("careflow-notifications", NotificationsMetrics.MeterName);
builder.Services.AddSingleton<NotificationsMetrics>();

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// Configuração do emissor em Authentication:Schemes:Bearer (Authority, ValidAudiences).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false);
builder.Services.AddAuthorizationBuilder().AddNotificationsPolicies();

builder.Services.AddHttpTenantContext();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<NotificationOptions>()
    .BindConfiguration(NotificationOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => o.RoundRetryDelay <= o.MaxRoundRetryDelay, "Notifications:RoundRetryDelay não pode passar do MaxRoundRetryDelay.")
    .ValidateOnStart();

builder.Services.AddNotificationsDatabase(builder.Configuration);

// Consome os eventos de consulta da agenda e de consentimento do Consent; publica a auditoria pelo Outbox.
builder.Services
    .AddCareFlowMessaging(builder.Configuration, DatabaseExtensions.RelayConnectionStringName)
    .AddOutbox()
    .AddConsumer<NotificationsDbContext>(consumers => consumers
        .Handle<AppointmentBooked, AppointmentBookedConsumer>()
        .Handle<AppointmentCancelled, AppointmentCancelledConsumer>()
        .Handle<ConsentGranted, ConsentGrantedConsumer>()
        .Handle<ConsentRevoked, ConsentRevokedConsumer>());

builder.Services.AddNotificationChannels();
builder.Services.AddContactDirectory(builder.Environment);
builder.Services.AddNotificationDelivery(builder.Configuration);

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

// Estado de cada canal para o Painel: 200 mesmo degradado (o serviço segue funcionando pelo fallback).
app.MapHealthChecks("/health/channels", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(ChannelsHealthCheck.Tag),
    ResponseWriter = static (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var entry = report.Entries.Values.FirstOrDefault();
        return context.Response.WriteAsJsonAsync(
            new { status = report.Status.ToString(), channels = entry.Data },
            JsonSerializerOptions.Web);
    },
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
});

app.MapNotificationEndpoints();

app.MapDemoPurgeIfEnabled(DatabaseExtensions.ConnectionStringName, AuthorizationPolicies.DemoPurge);

await app.MigrateIfEnabledAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

/// <summary>Ponto de entrada; público para os testes de integração com WebApplicationFactory.</summary>
public partial class Program;
