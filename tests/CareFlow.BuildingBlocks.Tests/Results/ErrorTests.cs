using CareFlow.BuildingBlocks.Results;

namespace CareFlow.BuildingBlocks.Tests.Results;

public sealed class ErrorTests
{
    public static TheoryData<Func<string, string, Error>, ErrorType> Factories => new()
    {
        { Error.Failure, ErrorType.Failure },
        { Error.Validation, ErrorType.Validation },
        { Error.NotFound, ErrorType.NotFound },
        { Error.Conflict, ErrorType.Conflict },
        { Error.Unauthorized, ErrorType.Unauthorized },
        { Error.Forbidden, ErrorType.Forbidden },
        { Error.PreconditionFailed, ErrorType.PreconditionFailed },
        { Error.PreconditionRequired, ErrorType.PreconditionRequired },
        { Error.Gone, ErrorType.Gone },
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public void Factory_SetsCodeMessageAndType(Func<string, string, Error> factory, ErrorType expectedType)
    {
        // Arrange
        const string code = "Agenda.SlotIndisponivel";
        const string message = "O horário escolhido não está mais disponível.";

        // Act
        var error = factory(code, message);

        // Assert
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
        Assert.Equal(expectedType, error.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Factory_WithBlankCode_Throws(string? code)
    {
        // Act
        var act = () => Error.Validation(code!, "mensagem");

        // Assert
        Assert.ThrowsAny<ArgumentException>(act);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Factory_WithBlankMessage_Throws(string? message)
    {
        // Act
        var act = () => Error.Validation("Codigo", message!);

        // Assert
        Assert.ThrowsAny<ArgumentException>(act);
    }

    [Fact]
    public void Errors_WithSameData_AreEqual()
    {
        // Arrange
        var first = Error.Conflict("Agenda.Conflito", "Conflito.");
        var second = Error.Conflict("Agenda.Conflito", "Conflito.");

        // Act & Assert
        Assert.Equal(first, second);
        Assert.NotEqual(Error.None, first);
    }
}
