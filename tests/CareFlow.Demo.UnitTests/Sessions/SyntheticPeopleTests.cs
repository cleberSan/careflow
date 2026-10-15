using CareFlow.Demo.Api.Sessions;

namespace CareFlow.Demo.UnitTests.Sessions;

public sealed class SyntheticPeopleTests
{
    [Fact]
    public void Create_UsesReservedDomain_AndSyntheticName()
    {
        // Act
        var person = SyntheticPeople.Create();

        // Assert
        Assert.StartsWith("Paciente Sintético ", person.FullName, StringComparison.Ordinal);
        Assert.EndsWith("@example.com", person.Email, StringComparison.Ordinal);
        Assert.StartsWith("551190000", person.Phone, StringComparison.Ordinal);
        Assert.Equal(13, person.Phone.Length);
        Assert.True(IsValidCpf(person.Cpf));
    }

    [Fact]
    public void Cpf_AlwaysHasValidCheckDigits_AndNeverRepeatsOneDigit()
    {
        for (var i = 0; i < 500; i++)
        {
            var cpf = SyntheticPeople.Cpf();

            Assert.Equal(11, cpf.Length);
            Assert.True(IsValidCpf(cpf), cpf);
            Assert.False(cpf[..9].All(c => c == cpf[0]), cpf);
        }
    }

    // Validação independente da implementação (algoritmo da Receita, pesos 10..2 e 11..2).
    private static bool IsValidCpf(string cpf)
    {
        if (cpf.Length != 11 || !cpf.All(char.IsAsciiDigit))
        {
            return false;
        }

        var digits = cpf.Select(c => c - '0').ToArray();
        for (var check = 9; check <= 10; check++)
        {
            var sum = 0;
            for (var i = 0; i < check; i++)
            {
                sum += digits[i] * (check + 1 - i);
            }

            var expected = sum * 10 % 11 % 10;
            if (digits[check] != expected)
            {
                return false;
            }
        }

        return true;
    }
}
