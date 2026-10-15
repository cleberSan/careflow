using CareFlow.Audit.Api.Common.Security;
using CareFlow.Audit.Api.Features.Entries;
using CareFlow.Audit.Api.Features.Verification;

namespace CareFlow.Audit.Api.Features;

internal static class AuditEndpoints
{
    public static IServiceCollection AddAuditFeatures(this IServiceCollection services) =>
        services
            .AddScoped<AuditWriter>()
            .AddScoped<ChainVerificationHandler>();

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var audit = app.MapGroup("/v1/audit").WithTags("Audit").RequireAuthorization(AuthorizationPolicies.Read);

        audit.MapGet("/entries", ListEntriesEndpoint.HandleAsync)
            .WithName("ListAuditEntries")
            .WithSummary("Trilha da clínica em ordem de gravação; filtre por resourceId e pagine com after/limit.");

        audit.MapGet("/verification", VerifyChainEndpoint.HandleAsync)
            .WithName("VerifyAuditChain")
            .WithSummary("Recalcula a cadeia de hash da clínica e aponta a primeira entrada adulterada.");

        return app;
    }
}
