using CareFlow.Contracts.Rights;
using CareFlow.Rights.Domain.Requests;

namespace CareFlow.Rights.Api.Features.Requests;

/// <summary>Nomes estáveis de tipo, situação e motivo, usados na API e nos eventos.</summary>
internal static class RequestNames
{
    public static string Of(SubjectRequestKind kind) => kind switch
    {
        SubjectRequestKind.Access => SubjectRequestKinds.Access,
        SubjectRequestKind.Portability => SubjectRequestKinds.Portability,
        SubjectRequestKind.Erasure => SubjectRequestKinds.Erasure,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Of(SubjectRequestStatus status) => status switch
    {
        SubjectRequestStatus.Received => "received",
        SubjectRequestStatus.InProgress => "in-progress",
        SubjectRequestStatus.Completed => "completed",
        SubjectRequestStatus.Denied => "denied",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static string Of(DenialReason reason) => reason switch
    {
        DenialReason.NoContact => "no-contact",
        DenialReason.CodeNotDelivered => "code-not-delivered",
        DenialReason.IdentityNotConfirmed => "identity-not-confirmed",
        DenialReason.SubjectNotFound => "subject-not-found",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    public static bool TryParseKind(string? value, out SubjectRequestKind kind)
    {
        (var known, kind) = value switch
        {
            SubjectRequestKinds.Access => (true, SubjectRequestKind.Access),
            SubjectRequestKinds.Portability => (true, SubjectRequestKind.Portability),
            SubjectRequestKinds.Erasure => (true, SubjectRequestKind.Erasure),
            _ => (false, default),
        };
        return known;
    }
}
