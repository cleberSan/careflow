using System.Globalization;
using System.Security.Cryptography;

namespace CareFlow.Testing;

/// <summary>
/// Dados pessoais 100% sintéticos para testes. CPFs são gerados aleatoriamente com dígitos verificadores
/// válidos; não vêm de nenhuma pessoa nem de nenhuma base real.
/// </summary>
public static class SyntheticPatients
{
    public static string Cpf(bool formatted = false)
    {
        Span<int> digits = stackalloc int[11];
        do
        {
            for (var i = 0; i < 9; i++)
            {
                digits[i] = RandomNumberGenerator.GetInt32(10);
            }
        }
        while (AllSame(digits[..9]));

        digits[9] = CheckDigit(digits[..9]);
        digits[10] = CheckDigit(digits[..10]);

        var value = string.Concat(digits.ToArray().Select(d => d.ToString(CultureInfo.InvariantCulture)));
        return formatted ? $"{value[..3]}.{value[3..6]}.{value[6..9]}-{value[9..]}" : value;
    }

    /// <summary>Nome único por chamada, para buscar o valor exato em bancos e logs.</summary>
    public static string FullName() => $"Paciente Sintetico {RandomToken()}";

    public static string Email() => $"sintetico.{RandomToken().ToLowerInvariant()}@example.com";

    public static string Phone() => "55119" + RandomNumberGenerator.GetInt32(10_000_000, 99_999_999).ToString(CultureInfo.InvariantCulture);

    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

    private static bool AllSame(ReadOnlySpan<int> digits)
    {
        foreach (var d in digits)
        {
            if (d != digits[0])
            {
                return false;
            }
        }

        return true;
    }

    private static int CheckDigit(ReadOnlySpan<int> digits)
    {
        var sum = 0;
        var weight = digits.Length + 1;
        foreach (var d in digits)
        {
            sum += d * weight--;
        }

        var remainder = sum * 10 % 11;
        return remainder == 10 ? 0 : remainder;
    }
}
