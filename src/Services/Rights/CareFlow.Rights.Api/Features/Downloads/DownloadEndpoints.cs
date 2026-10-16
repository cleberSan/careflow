using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Rights.Api.Common;
using CareFlow.Rights.Api.Common.Observability;
using CareFlow.Rights.Api.Features.Exports;
using CareFlow.Rights.Api.Features.Requests;
using CareFlow.Rights.Api.Infrastructure.Persistence;
using CareFlow.Rights.Domain.Requests;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Rights.Api.Features.Downloads;

internal static class CreateDownloadLinkEndpoint
{
    /// <summary>Gera um link de 15 minutos e uso único para o pacote. O token aparece só nesta resposta.</summary>
    public static async Task<Results<Created<DownloadLinkResponse>, ProblemHttpResult>> HandleAsync(
        Guid requestId,
        RightsDbContext db,
        HttpContext context,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var request = await db.Requests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return SubjectRequestErrors.NotFound.ToProblem();
        }

        var available = await db.Packages
            .AnyAsync(p => p.RequestId == requestId && p.DestroyedAt == null && p.ExpiresAt > now, cancellationToken)
            .ConfigureAwait(false);
        if (!available)
        {
            return SubjectRequestErrors.PackageUnavailable.ToProblem();
        }

        var (token, hash) = DownloadToken.Create(request.TenantId);
        var link = DownloadLink.Create(request.TenantId, request.Id, hash, now);
        db.DownloadLinks.Add(link);
        db.Record(request.TenantId, request.PatientRef, AuditActor.From(context.User), RightsAudit.DownloadLinkCreated, now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Created((string?)null, new DownloadLinkResponse(token, link.ExpiresAt));
    }
}

/// <summary>
/// Download pelo link, sem conta: o titular não tem login no sistema. O token vai no corpo (POST), nunca na URL, para
/// não parar em logs de proxy, histórico do navegador ou cabeçalho Referer. Link desconhecido, expirado ou já usado
/// recebem a mesma resposta.
/// </summary>
internal static partial class DownloadEndpoint
{
    public static async Task<Results<FileContentHttpResult, ProblemHttpResult>> HandleAsync(
        DownloadBody body,
        IServiceProvider services,
        HttpContext context,
        TimeProvider time,
        RightsMetrics metrics,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!DownloadToken.TryParse(body?.Token, out var tenantId, out var hash))
        {
            return LinkUnavailable();
        }

        // Anônimo: a clínica vem do token e entra no escopo antes da primeira consulta; o RLS faz o resto.
        services.GetRequiredService<TenantScope>().Enter(tenantId);
        var db = services.GetRequiredService<RightsDbContext>();
        var now = time.GetUtcNow();

        var link = await db.DownloadLinks.FirstOrDefaultAsync(l => l.TokenHash == hash, cancellationToken).ConfigureAwait(false);
        if (link is null || !link.TryRedeem(now))
        {
            return LinkUnavailable();
        }

        var package = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.RequestId == link.RequestId, cancellationToken).ConfigureAwait(false);
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == link.RequestId, cancellationToken).ConfigureAwait(false);
        if (package is null || request is null || !package.IsAvailable(now))
        {
            return LinkUnavailable();
        }

        var zip = services.GetRequiredService<PackageCipher>().Open(package.RequestId, package.Content!, package.WrappedKey!);
        db.Record(request.TenantId, request.PatientRef, RightsAudit.SubjectActor, RightsAudit.PackageDownloaded, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Outro download com o mesmo link chegou antes.
            return LinkUnavailable();
        }

        metrics.Downloaded();
        var logger = loggers.CreateLogger(typeof(DownloadEndpoint).FullName!);
        LogDownloaded(logger, zip.Length);
        return TypedResults.File(zip, "application/zip", $"careflow-pedido-{request.Id:N}.zip");
    }

    private static ProblemHttpResult LinkUnavailable() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "Link inválido, expirado ou já usado. Peça um novo link à clínica.",
            extensions: new Dictionary<string, object?> { ["code"] = "Direitos.LinkIndisponivel" });

    [LoggerMessage(EventId = 8010, Level = LogLevel.Information, Message = "Pacote do titular baixado ({Bytes} bytes)")]
    private static partial void LogDownloaded(ILogger logger, int bytes);
}
