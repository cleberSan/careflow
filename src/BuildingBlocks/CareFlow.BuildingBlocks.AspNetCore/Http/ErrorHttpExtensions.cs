using CareFlow.BuildingBlocks.Results;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;

namespace CareFlow.BuildingBlocks.AspNetCore.Http;

public static class ErrorHttpExtensions
{
    /// <summary>Converte um erro de negócio em ProblemDetails (RFC 9457), expondo o código estável em <c>code</c>.</summary>
    public static ProblemHttpResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = StatusCodeFor(error.Type);
        return TypedResults.Problem(
            statusCode: status,
            title: ReasonPhrases.GetReasonPhrase(status),
            detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorType.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        ErrorType.Gone => StatusCodes.Status410Gone,
        _ => StatusCodes.Status500InternalServerError,
    };
}
