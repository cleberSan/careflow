using CareFlow.Audit.Api.Infrastructure.Persistence;
using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Results;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Audit.Api.Features.Entries;

public sealed record AuditEntryResponse(
    long Sequence,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string SourceService,
    string Actor,
    string Action,
    string ResourceType,
    Guid ResourceId);

/// <summary>Página da trilha. <see cref="NextAfter"/> é o cursor da próxima página, nulo na última.</summary>
public sealed record AuditEntryPage(AuditEntryResponse[] Items, long? NextAfter);

internal static class ListEntriesEndpoint
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 200;

    public static readonly Error InvalidPage = Error.Validation(
        "Auditoria.PaginaInvalida", $"'after' não pode ser negativo e 'limit' deve ficar entre 1 e {MaximumLimit}.");

    /// <summary>Trilha da clínica em ordem de gravação, opcionalmente de um recurso só, com paginação por cursor.</summary>
    public static async Task<Results<Ok<AuditEntryPage>, ProblemHttpResult>> HandleAsync(
        Guid? resourceId,
        long? after,
        int? limit,
        AuditDbContext db,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var cursor = after ?? 0;
        var size = limit ?? DefaultLimit;
        if (cursor < 0 || size is < 1 or > MaximumLimit)
        {
            return InvalidPage.ToProblem();
        }

        var query = db.Entries.AsNoTracking().Where(e => e.Sequence > cursor);
        if (resourceId is not null)
        {
            query = query.Where(e => e.ResourceId == resourceId.Value);
        }

        // Busca um a mais para saber se há próxima página sem um COUNT.
        var rows = await query
            .OrderBy(e => e.Sequence)
            .Take(size + 1)
            .Select(e => new AuditEntryResponse(
                e.Sequence, e.OccurredAt, e.RecordedAt, e.SourceService, e.Actor, e.Action, e.ResourceType, e.ResourceId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > size;
        var items = hasMore ? rows[..size] : rows;
        response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new AuditEntryPage([.. items], hasMore ? items[^1].Sequence : null));
    }
}
