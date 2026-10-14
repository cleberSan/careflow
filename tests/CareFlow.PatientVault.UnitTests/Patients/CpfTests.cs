using CareFlow.PatientVault.Domain.Patients;
using CareFlow.Testing;

namespace CareFlow.PatientVault.UnitTests.Patients;

public sealed class CpfTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_ValidSyntheticCpf_NormalizesToDigits(bool formatted)
    {
        // Arrange
        var value = SyntheticPatients.Cpf(formatted);

        // Act
        var cpf = Cpf.Create(value);

        // Assert
        Assert.True(cpf.IsSuccess);
        Assert.Equal(value.Replace(".", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal), cpf.Value.Digits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234567890")]          // 10 dígitos
    [InlineData("123456789012")]        // 12 dígitos
    [InlineData("12345678900")]         // dígitos verificadores errados
    [InlineData("12345678919")]         // segundo dígito errado
    [InlineData("11111111111")]         // todos iguais passam no cálculo, mas não são válidos
    [InlineData("123.456.789-0a")]      // caractere inválido
    [InlineData("123/456/789-09")]      // separador inválido
    [InlineData("1234567890912345678901")] // longo demais
    public void Create_InvalidValue_ReturnsValidationError(string? value)
    {
        // Act
        var cpf = Cpf.Create(value);

        // Assert
        Assert.True(cpf.IsFailure);
        Assert.Equal(PatientErrors.InvalidCpf, cpf.Error);
    }

    [Fact]
    public void Create_KnownExample_IsValid()
    {
        // Act
        var cpf = Cpf.Create("123.456.789-09");

        // Assert
        Assert.True(cpf.IsSuccess);
    }

    [Fact]
    public void Create_CheckDigitZeroWhenRemainderIsTen()
    {
        // Arrange: base cujo primeiro cálculo dá resto 10 (dígito vira 0).
        var cpf = Cpf.Create("00000000604");

        // Assert
        Assert.True(cpf.IsSuccess);
    }

    [Fact]
    public void ToString_IsMasked()
    {
        // Arrange
        var digits = SyntheticPatients.Cpf();
        var cpf = Cpf.Create(digits).Value;

        // Act
        var text = $"{cpf}";

        // Assert
        Assert.DoesNotContain(digits, text, StringComparison.Ordinal);
        Assert.Equal("***.***.***-**", text);
    }

    [Fact]
    public void Equality_IsByDigits()
    {
        // Arrange
        var digits = SyntheticPatients.Cpf();
        var a = Cpf.Create(digits).Value;
        var b = Cpf.Create(SyntheticFormat(digits)).Value;

        // Assert
        Assert.Equal(a, b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals(null));
    }

    private static string SyntheticFormat(string d) => $"{d[..3]}.{d[3..6]}.{d[6..9]}-{d[9..]}";
}
