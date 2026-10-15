using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Notifications.Api.Infrastructure.Persistence;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Notifications.Api.Features.Notifications;

public sealed record DeliveryAttemptResponse(int Round, string Channel, string Outcome, string? Error, DateTimeOffset AttemptedAt);

public sealed record NotificationResponse(
    Guid Id,
    string Kind,
    string Status,
    DateTimeOffset DueAt,
    int Rounds,
    string? SentVia,
    string? NotSentReason,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<DeliveryAttemptResponse> Attempts);

internal static class ListAppointmentNotificationsEndpoint
{
    public static readonly Error NotFound = Error.NotFound(
        "Notificacoes.ConsultaSemAvisos",
        "Nenhum aviso encontrado para esta consulta nesta clínica.");

    /// <summary>
    /// Avisos de uma consulta e as tentativas de cada um, para a clínica e o Painel. Sem contato nem texto.
    /// Consulta de outra clínica responde 404, como uma inexistente (IDOR).
    /// </summary>
    public static async Task<Results<Ok<IReadOnlyList<NotificationResponse>>, ProblemHttpResult>> HandleAsync(
        Guid appointmentId,
        NotificationsDbContext db,
        CancellationToken cancellationToken)
    {
        var notifications = await db.Notifications.AsNoTracking()
            .Where(n => n.AppointmentId == appointmentId)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (notifications.Count == 0)
        {
            return NotFound.ToProblem();
        }

        var ids = notifications.Select(n => n.Id).ToArray();
        var attempts = (await db.DeliveryAttempts.AsNoTracking()
            .Where(a => ids.Contains(a.NotificationId))
            .OrderBy(a => a.AttemptedAt).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToLookup(a => a.NotificationId);

        IReadOnlyList<NotificationResponse> response =
        [
            .. notifications.Select(n => new NotificationResponse(
                n.Id,
                n.Kind.ToString(),
                n.Status.ToString(),
                n.DueAt,
                n.Rounds,
                n.SentVia?.ToString(),
                n.NotSentReason?.ToString(),
                n.CompletedAt,
                [.. attempts[n.Id].Select(a => new DeliveryAttemptResponse(a.Round, a.Channel.ToString(), a.Outcome.ToString(), a.Error, a.AttemptedAt))])),
        ];
        return TypedResults.Ok(response);
    }
}
