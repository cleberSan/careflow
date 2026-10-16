namespace CareFlow.Rights.Api.Features.Requests;

internal sealed record OpenRequestBody(Guid PatientRef, string? Kind);

internal sealed record ConfirmIdentityBody(string? Code);

internal sealed record ServiceProgress(string Service, string? Outcome, DateTimeOffset? AnsweredAt);

internal sealed record ReceiptLine(
    string Service, string Item, string Description, string Action, int Records, DateTimeOffset? RetainedUntil, string Basis);

/// <summary>Comprovante de eliminação: o que cada serviço apagou, cancelou ou manteve, e até quando. Só contagens.</summary>
internal sealed record ErasureReceipt(string PolicyVersion, DateTimeOffset ErasedAt, IReadOnlyList<ReceiptLine> Lines);

internal sealed record PackageInfo(bool Available, long Size, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DestroyedAt);

internal sealed record RequestResponse(
    Guid Id,
    Guid PatientRef,
    string Kind,
    string Status,
    string? DenialReason,
    DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt,
    bool Overdue,
    DateTimeOffset? CodeSentAt,
    string? CodeChannel,
    DateTimeOffset? IdentityConfirmedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<ServiceProgress> Services,
    ErasureReceipt? Receipt,
    PackageInfo? Package);

internal sealed record IdentityRejected(string Code, string Detail, int RemainingAttempts);

internal sealed record DownloadLinkResponse(string Token, DateTimeOffset ExpiresAt);

internal sealed record DownloadBody(string? Token);
