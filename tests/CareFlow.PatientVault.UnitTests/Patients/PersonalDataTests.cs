using CareFlow.PatientVault.Domain.Patients;
using CareFlow.Testing;

namespace CareFlow.PatientVault.UnitTests.Patients;

public sealed class PersonalDataTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private readonly Cpf _cpf = Cpf.Create(SyntheticPatients.Cpf()).Value;

    private Domain.Patients.PersonalData Valid(string? name = "Paciente Sintetico", DateOnly? birth = null, string? email = null, string? phone = null) =>
        Domain.Patients.PersonalData.Create(name, _cpf, birth ?? new DateOnly(1990, 5, 20), email, phone, Today).Value;

    [Fact]
    public void Create_ValidData_NormalizesFields()
    {
        // Act
        var data = PersonalData.Create("  Paciente   Sintetico  ", _cpf, new DateOnly(1990, 5, 20), " a.b@example.com ", "+55 (11) 90000-0000", Today);

        // Assert
        Assert.True(data.IsSuccess);
        Assert.Equal("Paciente Sintetico", data.Value.FullName);
        Assert.Equal("a.b@example.com", data.Value.Email);
        Assert.Equal("5511900000000", data.Value.Phone);
        Assert.Same(_cpf, data.Value.Cpf);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Nome\u0000Controle")]
    public void Create_InvalidName_Fails(string? name)
    {
        // Act
        var data = PersonalData.Create(name, _cpf, new DateOnly(1990, 1, 1), null, null, Today);

        // Assert
        Assert.Equal(PatientErrors.InvalidName, data.Error);
    }

    [Fact]
    public void Create_NameAtBoundaries()
    {
        // Assert
        Assert.True(PersonalData.Create(new string('a', PersonalData.MinNameLength), _cpf, new DateOnly(1990, 1, 1), null, null, Today).IsSuccess);
        Assert.True(PersonalData.Create(new string('a', PersonalData.MaxNameLength), _cpf, new DateOnly(1990, 1, 1), null, null, Today).IsSuccess);
        Assert.True(PersonalData.Create(new string('a', PersonalData.MaxNameLength + 1), _cpf, new DateOnly(1990, 1, 1), null, null, Today).IsFailure);
    }

    [Fact]
    public void Create_BirthDateBoundaries()
    {
        // Assert
        Assert.True(PersonalData.Create("Nome", _cpf, Today, null, null, Today).IsSuccess);
        Assert.Equal(PatientErrors.InvalidBirthDate, PersonalData.Create("Nome", _cpf, Today.AddDays(1), null, null, Today).Error);
        Assert.True(PersonalData.Create("Nome", _cpf, Today.AddYears(-PersonalData.MaxAgeYears), null, null, Today).IsSuccess);
        Assert.Equal(PatientErrors.InvalidBirthDate, PersonalData.Create("Nome", _cpf, Today.AddYears(-PersonalData.MaxAgeYears).AddDays(-1), null, null, Today).Error);
        Assert.Equal(PatientErrors.InvalidBirthDate, PersonalData.Create("Nome", _cpf, default, null, null, Today).Error);
    }

    [Theory]
    [InlineData("sem-arroba")]
    [InlineData("a@localhost")]
    [InlineData("Nome <a@example.com>")]
    [InlineData("a@@example.com")]
    public void Create_InvalidEmail_Fails(string email)
    {
        // Act
        var data = PersonalData.Create("Nome", _cpf, new DateOnly(1990, 1, 1), email, null, Today);

        // Assert
        Assert.Equal(PatientErrors.InvalidEmail, data.Error);
    }

    [Fact]
    public void Create_EmailTooLong_Fails()
    {
        // Arrange
        var email = new string('a', PersonalData.MaxEmailLength) + "@example.com";

        // Act
        var data = PersonalData.Create("Nome", _cpf, new DateOnly(1990, 1, 1), email, null, Today);

        // Assert
        Assert.Equal(PatientErrors.InvalidEmail, data.Error);
    }

    [Theory]
    [InlineData("123456789")]          // curto
    [InlineData("12345678901234")]     // longo
    [InlineData("11 9000a-0000")]      // letra
    [InlineData("11+900000000")]       // + fora do início
    public void Create_InvalidPhone_Fails(string phone)
    {
        // Act
        var data = PersonalData.Create("Nome", _cpf, new DateOnly(1990, 1, 1), null, phone, Today);

        // Assert
        Assert.Equal(PatientErrors.InvalidPhone, data.Error);
    }

    [Theory]
    [InlineData("1190000000", "1190000000")]
    [InlineData("+55 11 90000-0000", "5511900000000")]
    public void Create_ValidPhone_KeepsDigits(string phone, string expected)
    {
        // Act
        var data = Valid(phone: phone);

        // Assert
        Assert.Equal(expected, data.Phone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Create_OptionalContactsBlank_AreNull(string? blank)
    {
        // Act
        var data = Valid(email: blank, phone: blank);

        // Assert
        Assert.Null(data.Email);
        Assert.Null(data.Phone);
    }

    [Fact]
    public void ToString_DoesNotExposePersonalData()
    {
        // Arrange
        var name = SyntheticPatients.FullName();
        var email = SyntheticPatients.Email();
        var data = Valid(name: name, email: email);

        // Act
        var text = data.ToString();

        // Assert
        Assert.DoesNotContain(name, text, StringComparison.Ordinal);
        Assert.DoesNotContain(email, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_cpf.Digits, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NullCpf_Throws()
    {
        // Act
        var act = () => PersonalData.Create("Nome", null!, new DateOnly(1990, 1, 1), null, null, Today);

        // Assert
        Assert.Throws<ArgumentNullException>(act);
    }
}
