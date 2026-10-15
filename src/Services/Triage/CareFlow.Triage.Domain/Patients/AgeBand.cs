namespace CareFlow.Triage.Domain.Patients;

/// <summary>
/// Faixa etária que o modelo recebe no lugar da data de nascimento. As faixas seguem cortes que mudam a
/// conduta de triagem (lactente, criança, adolescente, adulto, idoso) e são largas o bastante para não
/// ajudar a identificar alguém. O cofre calcula a faixa; a data exata nunca sai de lá.
/// </summary>
public sealed record AgeBand
{
    private static readonly (int MinAge, string Name)[] Bands =
    [
        (0, "0-1"),
        (2, "2-11"),
        (12, "12-17"),
        (18, "18-39"),
        (40, "40-59"),
        (60, "60-79"),
        (80, "80+"),
    ];

    private AgeBand(string name, int minAge)
    {
        Name = name;
        MinAge = minAge;
    }

    public string Name { get; }

    /// <summary>Idade mínima da faixa, usada por regras que dependem da idade (febre em lactente, por exemplo).</summary>
    public int MinAge { get; }

    public bool IsInfant => MinAge == 0;

    public static IReadOnlyList<string> Names { get; } = [.. Bands.Select(b => b.Name)];

    public static AgeBand FromAge(int age)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(age);
        var band = Bands.Last(b => age >= b.MinAge);
        return new AgeBand(band.Name, band.MinAge);
    }

    public static AgeBand FromBirthDate(DateOnly birthDate, DateOnly today)
    {
        if (birthDate > today)
        {
            throw new ArgumentOutOfRangeException(nameof(birthDate), "Data de nascimento no futuro.");
        }

        var age = today.Year - birthDate.Year;
        if (today < birthDate.AddYears(age))
        {
            age--;
        }

        return FromAge(age);
    }

    public static bool TryParse(string? value, out AgeBand? band)
    {
        foreach (var (minAge, name) in Bands)
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                band = new AgeBand(name, minAge);
                return true;
            }
        }

        band = null;
        return false;
    }

    public override string ToString() => Name;
}
