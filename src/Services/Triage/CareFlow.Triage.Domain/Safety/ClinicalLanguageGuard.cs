using System.Text.RegularExpressions;

using CareFlow.Triage.Domain.Text;

namespace CareFlow.Triage.Domain.Safety;

/// <summary>
/// Barra, na justificativa do modelo, o que soaria como diagnóstico, prescrição ou promessa clínica. A sugestão
/// é só prioridade de atendimento; se a justificativa cruzar a linha, a sugestão inteira é descartada e a
/// triagem fica com as regras locais e com o profissional.
/// </summary>
public static partial class ClinicalLanguageGuard
{
    private static readonly (string Code, Regex Pattern)[] Rules =
    [
        ("diagnostico", Diagnosis()),
        ("prescricao", Prescription()),
        ("promessa", Promise()),
    ];

    public static IReadOnlyList<string> Codes { get; } = [.. Rules.Select(r => r.Code)];

    public static IReadOnlyList<string> Inspect(string rationale)
    {
        ArgumentNullException.ThrowIfNull(rationale);
        var folded = TextFolding.Fold(rationale);
        return [.. Rules.Where(r => r.Pattern.IsMatch(folded)).Select(r => r.Code)];
    }

    [GeneratedRegex(@"\bdiagnostic\w*|\bvoce\s+(tem|esta\s+com|sofre\s+de|teve)\b|\b(provavelmente|possivelmente|certamente|com\s+certeza)\s+(e|seja|tem|trata)\b|\btrata-se\s+de|\b(compativel|consistente|sugestiv\w*)\s+(com|de)\b|\bquadro\s+de\b|\b(e|seja)\s+(um|uma)\s+(caso\s+de\s+)?(infarto|avc|derrame|pneumonia|dengue|covid|gripe|apendicite|enxaqueca|virose|infeccao|crise|ataque)\b|\bindica\s+(um|uma)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Diagnosis();

    [GeneratedRegex(@"\b(tome|tomar|tomando|use|usar|aplique|aplicar|ingira|ingerir|receit\w*|prescrev\w*|prescric\w*|medicament\w*|remedio\w*|dose\w*|posologia|comprimidos?|\d+\s*mg\b|dipirona|paracetamol|ibuprofeno|antibiotic\w*|amoxicilina|antialergic\w*|anti-inflamatori\w*|xarope|pomada|cha\s+de|compressa)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Prescription();

    [GeneratedRegex(@"\b(nao\s+e\s+(nada\s+)?grave|nao\s+se\s+preocup\w*|fique\s+tranquil\w*|pode\s+ficar\s+tranquil\w*|garant\w*|vai\s+(melhorar|passar|ficar\s+bem)|sem\s+risco|nenhum\s+risco|cura\w*|curar)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Promise();
}
