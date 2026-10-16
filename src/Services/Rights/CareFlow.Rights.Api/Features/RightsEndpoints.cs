using CareFlow.Rights.Api.Common.Security;
using CareFlow.Rights.Api.Features.Downloads;
using CareFlow.Rights.Api.Features.Exports;
using CareFlow.Rights.Api.Features.Requests;

namespace CareFlow.Rights.Api.Features;

internal static class RightsEndpoints
{
    public const string DownloadRateLimit = "downloads";

    public static IServiceCollection AddRightsFeatures(this IServiceCollection services) =>
        services
            .AddScoped<OpenRequestHandler>()
            .AddScoped<ConfirmIdentityHandler>()
            .AddScoped<PackageCipher>()
            .AddSingleton<PackageSweeper>();

    public static IEndpointRouteBuilder MapRightsEndpoints(this IEndpointRouteBuilder app)
    {
        var requests = app.MapGroup("/v1/subject-requests").WithTags("SubjectRequests");

        requests.MapPost("/", OpenRequestEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Write)
            .WithName("OpenSubjectRequest")
            .WithSummary("Recebe um pedido (access, portability, erasure) e manda o código de confirmação ao contato do cadastro.");

        requests.MapGet("/{requestId:guid}", GetRequestEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Read)
            .WithName("GetSubjectRequest")
            .WithSummary("Situação, prazo, andamento por serviço, comprovante de eliminação e situação do pacote.");

        requests.MapPost("/{requestId:guid}/identity-confirmation", ConfirmIdentityEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Write)
            .WithName("ConfirmSubjectIdentity")
            .WithSummary("Confere o código de 6 dígitos. Confirmado, o pedido segue para todos os serviços.");

        requests.MapPost("/{requestId:guid}/download-links", CreateDownloadLinkEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Write)
            .WithName("CreateDownloadLink")
            .WithSummary("Gera um link de 15 minutos e uso único para o pacote.");

        requests.MapPost("/{requestId:guid}/erasure-replay", ReplayErasureEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithName("ReplayErasure")
            .WithSummary("Reaplica uma eliminação concluída depois de restaurar um backup.");

        app.MapPost("/v1/downloads", DownloadEndpoint.HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(DownloadRateLimit)
            .WithTags("Downloads")
            .WithName("DownloadPackage")
            .WithSummary("Baixa o pacote com o token do link (no corpo). Uso único.");

        return app;
    }
}
