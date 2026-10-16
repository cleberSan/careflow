using System.Text.Json;

using CareFlow.Contracts.Rights;
using CareFlow.Rights.Domain.Requests;

namespace CareFlow.Rights.Api.Features.Requests;

/// <summary>Monta a resposta do pedido: andamento por serviço, comprovante de eliminação e situação do pacote.</summary>
internal static class RequestView
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RequestResponse From(
        SubjectRequest request,
        IReadOnlyList<SubjectRequestAnswer> answers,
        PackageInfo? package,
        ErasureReceipt? receipt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(answers);

        // Antes da confirmação de identidade nenhum serviço foi acionado: a lista fica vazia.
        IReadOnlyList<ServiceProgress> services = request.IdentityConfirmedAt is null
            ? []
            : [.. SubjectRequestServices.All.Select(service =>
            {
                var answer = answers.FirstOrDefault(a => a.Service == service);
                return new ServiceProgress(service, answer?.Outcome, answer?.AnsweredAt);
            })];

        return new RequestResponse(
            request.Id,
            request.PatientRef,
            RequestNames.Of(request.Kind),
            RequestNames.Of(request.Status),
            request.DenialReason is { } reason ? RequestNames.Of(reason) : null,
            request.ReceivedAt,
            request.DueAt,
            request.IsOverdue(now),
            request.CodeSentAt,
            request.CodeChannel,
            request.IdentityConfirmedAt,
            request.CompletedAt,
            services,
            receipt,
            package);
    }

    /// <summary>
    /// Comprovante a partir das linhas que cada serviço mandou e da política vigente no pedido. Só existe para
    /// eliminação concluída.
    /// </summary>
    public static ErasureReceipt? Receipt(SubjectRequest request, IReadOnlyList<SubjectRequestAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(answers);
        if (request.Kind != SubjectRequestKind.Erasure || request.Status != SubjectRequestStatus.Completed || request.CompletedAt is not { } erasedAt)
        {
            return null;
        }

        var lines = new List<ReceiptLine>();
        foreach (var service in SubjectRequestServices.All)
        {
            var answer = answers.FirstOrDefault(a => a.Service == service);
            var reported = answer?.Erasure is { } erasure
                ? JsonSerializer.Deserialize<List<ErasureLine>>(erasure, Json) ?? []
                : [];

            foreach (var rule in RetentionPolicy.For(service))
            {
                var records = reported.Where(line => line.Item == rule.Item).Sum(line => line.Records);
                lines.Add(new ReceiptLine(service, rule.Item, rule.Description, rule.Action, records, rule.RetainedUntil(erasedAt), rule.Basis));
            }
        }

        return new ErasureReceipt(request.PolicyVersion ?? RetentionPolicy.Version, erasedAt, lines);
    }
}
