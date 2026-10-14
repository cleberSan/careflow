using System.Text.Json.Serialization;

using CareFlow.Scheduling.Api.Common.Observability;
using CareFlow.Scheduling.Api.Common.Security;
using CareFlow.Scheduling.Api.Common.Tenancy;
using CareFlow.Scheduling.Api.Features.Appointments;
using CareFlow.Scheduling.Api.Infrastructure;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.AddObservability();

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// Configuração do emissor em Authentication:Schemes:Bearer (Authority, ValidAudiences).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.MapInboundClaims = false);
builder.Services.AddAuthorizationBuilder().AddSchedulingPolicies();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
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
builder.Services.AddHealthChecks().AddDbContextCheck<SchedulingDbContext>(tags: ["ready"]);

builder.Services.AddAppointmentFeatures();

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

await app.MigrateIfEnabledAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

/// <summary>Ponto de entrada; público para os testes de integração com WebApplicationFactory.</summary>
public partial class Program;
