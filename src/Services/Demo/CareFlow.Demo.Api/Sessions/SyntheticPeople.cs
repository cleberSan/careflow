using System.Globalization;
using System.Security.Cryptography;

using CareFlow.Demo.Api.Services;

namespace CareFlow.Demo.Api.Sessions;

/// <summary>
/// Paciente 100% sintético para cada sessão: nome com sufixo aleatório, e-mail em <c>example.com</c> (reservado
/// pela RFC 2606), telefone de uma faixa fixa e CPF aleatório com dígitos verificadores válidos, que não vem de
/// nenhuma base real. Os provedores do demo são simulados: nada é enviado a esse e-mail ou telefone.
/// </summary>
internal static class SyntheticPeople
{
    public static SyntheticPerson Create()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(2));
        return new SyntheticPerson(
            $"Paciente Sintético {token}",
            Cpf(),
            new DateOnly(1990, 5, 17),
            $"paciente.sintetico.{token.ToLowerInvariant()}@example.com",
            "551190000" + RandomNumberGenerator.GetInt32(1_000, 10_000).ToString(CultureInfo.InvariantCulture));
    }

    public static string Cpf()
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
        return string.Concat(digits.ToArray().Select(d => d.ToString(CultureInfo.InvariantCulture)));
    }

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
