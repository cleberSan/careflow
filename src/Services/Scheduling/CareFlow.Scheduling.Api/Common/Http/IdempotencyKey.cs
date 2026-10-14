using System.Text.RegularExpressions;

using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Scheduling.Api.Common.Http;

/// <summary>
/// Cabeçalho <c>Idempotency-Key</c> (draft-ietf-httpapi-idempotency-key-header): obrigatório nas criações,
/// com até 64 caracteres seguros para log e índice.
/// </summary>
internal static partial class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    public static readonly Error Invalid = Error.Validation(
        "Idempotencia.ChaveInvalida",
        "Envie o cabeçalho Idempotency-Key com 1 a 64 caracteres entre letras, números, '-' e '_'.");

    public static readonly Error ReusedWithDifferentPayload = Error.Conflict(
        "Idempotencia.ChaveReutilizada",
        "Esta Idempotency-Key já foi usada com outro conteúdo.");

    public static Result<string> Validate(string? value) =>
        value is not null && Pattern().IsMatch(value) ? Result.Success(value) : Result.Failure<string>(Invalid);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
