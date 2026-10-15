using CareFlow.BuildingBlocks.Results;

namespace CareFlow.PatientVault.Domain.Patients;

/// <summary>
/// CPF validado e normalizado (11 dígitos). <see cref="ToString"/> é mascarado para que o valor
/// nunca vaze por interpolação em logs ou mensagens; use <see cref="Digits"/> de forma explícita.
/// </summary>
public sealed class Cpf : IEquatable<Cpf>
{
    public const int Length = 11;

    private Cpf(string digits)
    {
        Digits = digits;
    }

    public string Digits { get; }

    /// <summary>Aceita o CPF com ou sem pontuação (<c>000.000.000-00</c>).</summary>
    public static Result<Cpf> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 20)
        {
            return PatientErrors.InvalidCpf;
        }

        Span<char> digits = stackalloc char[Length];
        var count = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                if (count == Length)
                {
                    return PatientErrors.InvalidCpf;
                }

                digits[count++] = c;
            }
            else if (c is not ('.' or '-' or ' '))
            {
                return PatientErrors.InvalidCpf;
            }
        }

        if (count != Length || AllSame(digits) || !HasValidCheckDigits(digits))
        {
            return PatientErrors.InvalidCpf;
        }

        return new Cpf(new string(digits));
    }

    public bool Equals(Cpf? other) => other is not null && string.Equals(Digits, other.Digits, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as Cpf);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Digits);

    public override string ToString() => "***.***.***-**";

    private static bool AllSame(ReadOnlySpan<char> digits)
    {
        foreach (var c in digits)
        {
            if (c != digits[0])
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasValidCheckDigits(ReadOnlySpan<char> digits) =>
        CheckDigit(digits[..9]) == digits[9] - '0'
        && CheckDigit(digits[..10]) == digits[10] - '0';

    private static int CheckDigit(ReadOnlySpan<char> digits)
    {
        var sum = 0;
        var weight = digits.Length + 1;
        foreach (var c in digits)
        {
            sum += (c - '0') * weight--;
        }

        var remainder = sum * 10 % 11;
        return remainder == 10 ? 0 : remainder;
    }
}
