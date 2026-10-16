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
        ("diagnostico", NamedCondition()),
        ("prescricao", Prescription()),
        ("promessa", Promise()),
    ];

    public static IReadOnlyList<string> Codes { get; } = [.. Rules.Select(r => r.Code).Distinct()];

    public static IReadOnlyList<string> Inspect(string rationale)
    {
        ArgumentNullException.ThrowIfNull(rationale);
        var folded = TextFolding.Fold(rationale);
        return [.. Rules.Where(r => r.Pattern.IsMatch(folded)).Select(r => r.Code).Distinct()];
    }

    [GeneratedRegex(@"\bdiagnostic\w*|\bvoce\s+(tem|esta\s+com|sofre\s+de|teve)\b|\b(provavelmente|possivelmente|certamente|com\s+certeza)\s+(e|seja|tem|trata)\b|\btrata-se\s+de|\b(compativ\w*|consistent\w*|sugestiv\w*)\s+(com|de)\b|\bquadro\s+de\b|\b(e|seja)\s+(um|uma)\s+(caso\s+de\s+)?(infarto|avc|derrame|pneumonia|dengue|covid|gripe|apendicite|enxaqueca|virose|infeccao|crise|ataque)\b|\bindica\s+(um|uma)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Diagnosis();

    // Nomear a doença, mesmo como hipótese ("possível AVC", "sugere fratura", "descartar lesão intracraniana"), já é
    // diagnóstico. A justificativa fala do sintoma e da urgência, nunca da causa.
    [GeneratedRegex(@"\b(avc|derrame|infarto|fratur\w*|apendicite|pneumonia|meningite|sepse|septic\w*|embolia|trombose|anafilax\w*|dengue|covid|enxaqueca|virose|gastroenterite|pielonefrite|cistite|otite|amigdalite|sinusite|asma\s+grave|lesao\s+intracranian\w*|hemorragia\s+(cerebral|interna|intracranian\w*)|(condicao|causa|doenca|problema|evento)\s+cardiac\w*|(doenca|condicao)\s+grave)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NamedCondition();

    [GeneratedRegex(@"\b(tome|tomar|tomando|use|usar|aplique|aplicar|ingira|ingerir|receit\w*|prescrev\w*|prescric\w*|medicament\w*|remedio\w*|dose\w*|posologia|comprimidos?|\d+\s*mg\b|dipirona|paracetamol|ibuprofeno|antibiotic\w*|amoxicilina|antialergic\w*|anti-inflamatori\w*|xarope|pomada|cha\s+de|compressa)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Prescription();

    [GeneratedRegex(@"\b(nao\s+e\s+(nada\s+)?grave|nao\s+se\s+preocup\w*|fique\s+tranquil\w*|pode\s+ficar\s+tranquil\w*|garant\w*|vai\s+(melhorar|passar|ficar\s+bem)|sem\s+risco|nenhum\s+risco|cura\w*|curar)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Promise();
}
