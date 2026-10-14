using CareFlow.BuildingBlocks.Results;

namespace CareFlow.BuildingBlocks.Tests.Results;

public sealed class ResultTests
{
    private static readonly Error SampleError = Error.NotFound("Paciente.NaoEncontrado", "Paciente não encontrado.");

    [Fact]
    public void Success_IsSuccessWithoutError()
    {
        // Act
        var result = Result.Success();

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_IsFailureWithError()
    {
        // Act
        var result = Result.Failure(SampleError);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(SampleError, result.Error);
    }

    [Fact]
    public void Failure_WithNoneError_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));
    }

    [Fact]
    public void Failure_WithNullError_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void GenericSuccess_ExposesValue()
    {
        // Act
        var result = Result.Success(42);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void GenericSuccess_WithNullValue_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => Result.Success<string>(null!));
    }

    [Fact]
    public void GenericFailure_ReadingValue_Throws()
    {
        // Arrange
        var result = Result.Failure<int>(SampleError);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversions_CreateSuccessAndFailure()
    {
        // Act
        Result<string> success = "ok";
        Result<string> failure = SampleError;

        // Assert
        Assert.True(success.IsSuccess);
        Assert.Equal("ok", success.Value);
        Assert.True(failure.IsFailure);
        Assert.Equal(SampleError, failure.Error);
    }

    [Theory]
    [InlineData(true, "sucesso:7")]
    [InlineData(false, "falha:Paciente.NaoEncontrado")]
    public void Match_CallsBranchForState(bool isSuccess, string expected)
    {
        // Arrange
        var result = isSuccess ? Result.Success(7) : Result.Failure<int>(SampleError);

        // Act
        var output = result.Match(value => $"sucesso:{value}", error => $"falha:{error.Code}");

        // Assert
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Match_WithNullHandler_Throws()
    {
        // Arrange
        var result = Result.Success(1);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => result.Match<int>(null!, _ => 0));
        Assert.Throws<ArgumentNullException>(() => result.Match(v => v, null!));
    }
}
