namespace CareFlow.Triage.Domain.Pseudonymization;

/// <summary>
/// O que o cofre sabe do paciente e que pode aparecer escrito na queixa. Fica só em memória durante a
/// pseudonimização; nunca é gravado pelo Triage nem enviado ao modelo.
/// </summary>
public sealed class KnownIdentity
{
    public static readonly KnownIdentity None = new([]);

    public KnownIdentity(IEnumerable<string> nameParts)
    {
        ArgumentNullException.ThrowIfNull(nameParts);
        NameParts = [.. nameParts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];
    }

    /// <summary>Partes do nome completo e do nome social (preposições como "da" são ignoradas na busca).</summary>
    public IReadOnlyList<string> NameParts { get; }

    public static KnownIdentity FromFullName(string? fullName) =>
        string.IsNullOrWhiteSpace(fullName) ? None : new KnownIdentity(fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public override string ToString() => "KnownIdentity { *** }";
}
