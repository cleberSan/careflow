namespace CareFlow.BuildingBlocks.Results;

/// <summary>
/// Erro de negócio esperado. Mensagens não devem conter dados pessoais, pois podem chegar a logs e respostas HTTP.
/// </summary>
public sealed record Error
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);

    private Error(string code, string message, ErrorType type)
    {
        Code = code;
        Message = message;
        Type = type;
    }

    public string Code { get; }

    public string Message { get; }

    public ErrorType Type { get; }

    public static Error Failure(string code, string message) => Create(code, message, ErrorType.Failure);

    public static Error Validation(string code, string message) => Create(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => Create(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => Create(code, message, ErrorType.Conflict);

    public static Error Unauthorized(string code, string message) => Create(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => Create(code, message, ErrorType.Forbidden);

    /// <summary>A versão informada pelo cliente (If-Match) não é mais a atual.</summary>
    public static Error PreconditionFailed(string code, string message) => Create(code, message, ErrorType.PreconditionFailed);

    /// <summary>A operação exige uma pré-condição (If-Match) que o cliente não enviou.</summary>
    public static Error PreconditionRequired(string code, string message) => Create(code, message, ErrorType.PreconditionRequired);

    /// <summary>O recurso existiu e foi eliminado; não volta.</summary>
    public static Error Gone(string code, string message) => Create(code, message, ErrorType.Gone);

    private static Error Create(string code, string message, ErrorType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new Error(code, message, type);
    }
}
