using CareFlow.Consent.Api.Common.Security;

namespace CareFlow.Consent.Api.Features.Consents;

internal static class ConsentEndpoints
{
    public static IServiceCollection AddConsentFeatures(this IServiceCollection services) =>
        services
            .AddScoped<GrantConsentHandler>()
            .AddScoped<RevokeConsentHandler>();

    public static IEndpointRouteBuilder MapConsentEndpoints(this IEndpointRouteBuilder app)
    {
        var consents = app.MapGroup("/v1/patients/{patientRef:guid}/consents").WithTags("Consents");

        consents.MapGet("/", ListConsentsEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Read)
            .WithName("ListConsents")
            .WithSummary("Situação do paciente em cada finalidade.");

        consents.MapPut("/{purpose}", GrantConsentEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Write)
            .WithName("GrantConsent")
            .WithSummary("Concede a finalidade (scheduling, notifications, ai-triage) com base legal e versão do termo. Idempotente.");

        consents.MapPost("/{purpose}/revoke", RevokeConsentEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Write)
            .WithName("RevokeConsent")
            .WithSummary("Revoga a finalidade e propaga ConsentRevoked. Idempotente.");

        return app;
    }
}
