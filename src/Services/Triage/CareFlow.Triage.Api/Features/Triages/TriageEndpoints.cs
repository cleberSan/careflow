using System.Globalization;
using System.Threading.RateLimiting;

using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.Triage.Api.Common.Security;

using Microsoft.Extensions.Options;

namespace CareFlow.Triage.Api.Features.Triages;

internal static class TriageEndpoints
{
    public const string SubmissionPolicy = "triage-submissions";

    public static IServiceCollection AddTriageFeatures(this IServiceCollection services)
    {
        services.AddOptions<TriageOptions>()
            .BindConfiguration(TriageOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            // Por clínica (claim validada pelo JWT), não por endereço: o custo do modelo é da clínica.
            limiter.AddPolicy(SubmissionPolicy, context =>
            {
                var perMinute = context.RequestServices.GetRequiredService<IOptions<TriageOptions>>().Value.SubmissionsPerClinicPerMinute;
                var clinic = ClaimsPrincipalExtensions.TryGetTenantId(context.User, out var tenantId) ? tenantId : Guid.Empty;
                return RateLimitPartition.GetFixedWindowLimiter(clinic, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = perMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });

        return services
            .AddScoped<SubmitTriageHandler>()
            .AddScoped<ReviewTriageHandler>();
    }

    public static IEndpointRouteBuilder MapTriageEndpoints(this IEndpointRouteBuilder app)
    {
        var triages = app.MapGroup("/v1/triages").WithTags("Triages");

        triages.MapPost("/", SubmitTriageEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Submit)
            .RequireRateLimiting(SubmissionPolicy)
            .WithName("SubmitTriage")
            .WithSummary("Avalia a queixa e sugere uma prioridade de atendimento, pendente de revisão humana. Não é diagnóstico.");

        triages.MapGet("/{id:guid}", GetTriageEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Read)
            .WithName("GetTriage")
            .WithSummary("Queixa, o que saiu para o modelo, sugestão, proteções acionadas e revisão.");

        triages.MapPost("/{id:guid}/review", ReviewTriageEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Review)
            .WithName("ReviewTriage")
            .WithSummary("Profissional confirma a sugestão ou corrige a prioridade.");

        app.MapGet("/v1/patients/{patientRef:guid}/ai-consent", Consents.GetAiConsentEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Read)
            .WithTags("Consents")
            .WithName("GetAiConsent")
            .WithSummary("Estado do consentimento ai-triage como o Triage vê agora (granted, revoked ou unknown).");

        return app;
    }
}
