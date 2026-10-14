namespace CareFlow.BuildingBlocks.Results;

/// <summary>
/// Categoria de um erro de negócio. Usada pela borda HTTP para escolher o status do ProblemDetails.
/// </summary>
public enum ErrorType
{
    None = 0,
    Failure,
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
    PreconditionFailed,
    PreconditionRequired,
}
