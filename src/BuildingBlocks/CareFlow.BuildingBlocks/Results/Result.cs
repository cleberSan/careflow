namespace CareFlow.BuildingBlocks.Results;

/// <summary>
/// Resultado de uma operação que pode falhar por regra de negócio, sem usar exceptions para controle de fluxo.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (isSuccess && error != Error.None)
        {
            throw new ArgumentException("Um resultado de sucesso não pode carregar erro.", nameof(error));
        }

        if (!isSuccess && error == Error.None)
        {
            throw new ArgumentException("Um resultado de falha precisa de um erro.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => Result<TValue>.Success(value);

    public static Result<TValue> Failure<TValue>(Error error) => Result<TValue>.Failure(error);
}

/// <summary>
/// Resultado com valor. Ler <see cref="Value"/> de uma falha é erro de programação e lança exceção.
/// </summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Não é possível ler o valor de um resultado de falha.");

    internal static Result<TValue> Success(TValue value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "Use um Error.NotFound em vez de sucesso com valor nulo.");
        }

        return new Result<TValue>(value, true, Error.None);
    }

    internal static new Result<TValue> Failure(Error error) => new(default, false, error);

    public TOut Match<TOut>(Func<TValue, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return IsSuccess ? onSuccess(_value!) : onFailure(Error);
    }

#pragma warning disable CA2225 // Conversões implícitas são o objetivo aqui; Success/Failure são as alternativas nomeadas.
    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure(error);
#pragma warning restore CA2225
}
