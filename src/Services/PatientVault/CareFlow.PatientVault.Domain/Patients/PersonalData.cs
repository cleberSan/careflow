using System.Net.Mail;

using CareFlow.BuildingBlocks.Results;

namespace CareFlow.PatientVault.Domain.Patients;

/// <summary>
/// Dados pessoais validados de um paciente. Só existem dentro do cofre: os outros serviços
/// conhecem apenas o <c>patient_ref</c>. <see cref="ToString"/> é redigido para não vazar em logs.
/// </summary>
public sealed record PersonalData
{
    public const int MinNameLength = 2;
    public const int MaxNameLength = 150;
    public const int MaxEmailLength = 254;
    public const int MaxAgeYears = 130;

    private PersonalData(string fullName, Cpf cpf, DateOnly birthDate, string? email, string? phone)
    {
        FullName = fullName;
        Cpf = cpf;
        BirthDate = birthDate;
        Email = email;
        Phone = phone;
    }

    public string FullName { get; }

    public Cpf Cpf { get; }

    public DateOnly BirthDate { get; }

    public string? Email { get; }

    /// <summary>Somente dígitos, com DDD (e DDI opcional).</summary>
    public string? Phone { get; }

    public static Result<PersonalData> Create(
        string? fullName,
        Cpf cpf,
        DateOnly birthDate,
        string? email,
        string? phone,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(cpf);

        var name = NormalizeName(fullName);
        if (name is null)
        {
            return PatientErrors.InvalidName;
        }

        if (birthDate > today || birthDate < today.AddYears(-MaxAgeYears))
        {
            return PatientErrors.InvalidBirthDate;
        }

        string? normalizedEmail = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            normalizedEmail = NormalizeEmail(email);
            if (normalizedEmail is null)
            {
                return PatientErrors.InvalidEmail;
            }
        }

        string? normalizedPhone = null;
        if (!string.IsNullOrWhiteSpace(phone))
        {
            normalizedPhone = NormalizePhone(phone);
            if (normalizedPhone is null)
            {
                return PatientErrors.InvalidPhone;
            }
        }

        return new PersonalData(name, cpf, birthDate, normalizedEmail, normalizedPhone);
    }

    public override string ToString() => "PersonalData { *** }";

    private static string? NormalizeName(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (trimmed.Length is < MinNameLength or > MaxNameLength)
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (char.IsControl(c))
            {
                return null;
            }
        }

        return trimmed;
    }

    private static string? NormalizeEmail(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > MaxEmailLength
            || !MailAddress.TryCreate(trimmed, out var address)
            || !string.Equals(address.Address, trimmed, StringComparison.Ordinal)
            || !address.Host.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }

    private static string? NormalizePhone(string value)
    {
        Span<char> digits = stackalloc char[13];
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsAsciiDigit(c))
            {
                if (count == digits.Length)
                {
                    return null;
                }

                digits[count++] = c;
            }
            else if (!(c is ' ' or '-' or '(' or ')' || (c == '+' && count == 0)))
            {
                return null;
            }
        }

        return count >= 10 ? new string(digits[..count]) : null;
    }
}
