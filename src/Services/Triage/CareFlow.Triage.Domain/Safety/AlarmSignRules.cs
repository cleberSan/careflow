using System.Text.RegularExpressions;

using CareFlow.Triage.Domain.Patients;
using CareFlow.Triage.Domain.Priorities;
using CareFlow.Triage.Domain.Text;

namespace CareFlow.Triage.Domain.Safety;

/// <summary>
/// Regras determinísticas de sinais de alarme. Rodam sobre o texto original (antes da pseudonimização) e só
/// sobem a prioridade: o resultado final é o maior entre a sugestão do modelo e o piso das regras, então o
/// modelo nunca rebaixa um alarme. Negações ("sem dor no peito") não são tratadas de propósito: na dúvida,
/// a regra sobe a prioridade e o profissional corrige. A lista é um ponto de partida de demonstração, não um
/// protocolo clínico validado.
/// </summary>
public static partial class AlarmSignRules
{
    private static readonly (string Code, TriagePriority Floor, Regex Pattern)[] Rules =
    [
        ("dor-no-peito", TriagePriority.Emergency, ChestPain()),
        ("falta-de-ar", TriagePriority.Emergency, Breathlessness()),
        ("sinais-de-avc", TriagePriority.Emergency, StrokeSigns()),
        ("perda-de-consciencia", TriagePriority.Emergency, Unconsciousness()),
        ("convulsao", TriagePriority.Emergency, Seizure()),
        ("sangramento-intenso", TriagePriority.Emergency, SevereBleeding()),
        ("reacao-alergica-grave", TriagePriority.Emergency, Anaphylaxis()),
        ("risco-de-suicidio", TriagePriority.Emergency, SuicideRisk()),
        ("sangramento-na-gestacao", TriagePriority.Emergency, PregnancyBleeding()),
        ("dor-de-cabeca-subita", TriagePriority.Urgent, ThunderclapHeadache()),
        ("febre-alta", TriagePriority.Urgent, HighFever()),
        ("dor-abdominal-intensa", TriagePriority.Urgent, SevereAbdominalPain()),
        ("sangue-nas-fezes-ou-urina", TriagePriority.Urgent, BloodInStoolOrUrine()),
        ("trauma-na-cabeca", TriagePriority.Urgent, HeadTrauma()),
    ];

    private static readonly Regex Fever = FeverWord();

    /// <summary>Todos os códigos que as regras conhecem, para documentação e avaliação.</summary>
    public static IReadOnlyList<string> Codes { get; } = [.. Rules.Select(r => r.Code), "febre-em-lactente"];

    public static IReadOnlyList<AlarmSign> Evaluate(string complaint, AgeBand ageBand)
    {
        ArgumentNullException.ThrowIfNull(complaint);
        ArgumentNullException.ThrowIfNull(ageBand);

        var folded = TextFolding.Fold(complaint);
        var signs = new List<AlarmSign>();
        foreach (var (code, floor, pattern) in Rules)
        {
            if (pattern.IsMatch(folded))
            {
                signs.Add(new AlarmSign(code, floor));
            }
        }

        if (ageBand.IsInfant && Fever.IsMatch(folded))
        {
            signs.Add(new AlarmSign("febre-em-lactente", TriagePriority.Urgent));
        }

        return signs;
    }

    /// <summary>A prioridade mínima imposta pelos sinais, ou nenhuma.</summary>
    public static TriagePriority? FloorOf(IEnumerable<AlarmSign> signs) =>
        signs.Select(s => (TriagePriority?)s.Floor).DefaultIfEmpty(null).Max();

    [GeneratedRegex(@"\b(dor|dores|aperto|pressao|peso|queimacao)\s+(forte\s+|intensa\s+)?(no|na|em|do)\s+(meu\s+)?(peito|torax)|\bdor\s+toracica|\bpeito\s+(apertado|doendo)|\binfart", RegexOptions.CultureInvariant)]
    private static partial Regex ChestPain();

    [GeneratedRegex(@"\bfalta\s+de\s+ar\b|\bfaltando\s+(o\s+)?ar\b|\bdificuldade\s+(para|pra|de|em)\s+respirar|\bnao\s+(consigo|consegue|conseguia)\s+respirar|\bsem\s+conseguir\s+respirar|\bsufoc|\brespiracao\s+(curta|ofegante|dificil)|\bfalta\s+de\s+folego|\blabios?\s+(roxos?|azulados?)|\brespir\w*\s+com\s+(muita\s+)?dificuldade|\bpuxando\s+(o\s+)?ar|\bsem\s+ar\b", RegexOptions.CultureInvariant)]
    private static partial Regex Breathlessness();

    [GeneratedRegex(@"\bboca\s+torta|\brosto\s+(caido|torto|paralisado)|\bfala\s+(enrolada|arrastada|embolada)|\bnao\s+consigo\s+falar|\bfraqueza\s+(de|em|no|na|num|numa)\s+(um\s+|uma\s+)?(lado|braco|perna)|\b(lado|metade)\s+do\s+corpo\s+(dormente|paralisad|fraco|sem\s+forca)|\bperdi\s+a\s+forca\s+(do|no|na|de)\s+(braco|perna|mao)|\bavc\b|\bderrame", RegexOptions.CultureInvariant)]
    private static partial Regex StrokeSigns();

    [GeneratedRegex(@"\bdesmai|\bperd(i|eu)\s+(a\s+)?consciencia|\bdesacordad|\binconsciente|\bapaguei\b|\bnao\s+(acorda|responde)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Unconsciousness();

    [GeneratedRegex(@"\bconvuls|\b(crise|ataque)\s+(epileptic|convulsiv)|\bepilepsia", RegexOptions.CultureInvariant)]
    private static partial Regex Seizure();

    [GeneratedRegex(@"\bsangramento\s+(forte|intenso|que\s+nao\s+(para|passa))|\bsangrando\s+muito|\bmuito\s+sangue|\bhemorragia|\b(vomit\w*|tossindo|tossi)\s+sangue|\bhemorroidas?\s+sangrando\s+muito", RegexOptions.CultureInvariant)]
    private static partial Regex SevereBleeding();

    [GeneratedRegex(@"\bgarganta\s+(esta\s+|ta\s+)?(fechando|fechou|inchada)|\b(inchaco|inchou|inchad\w*)\s+(na|no|da|do|nos|nas)\s+(garganta|lingua|labios?|boca|rosto|olhos)|\b(lingua|labios?|boca|rosto)\s+inchad|\banafila", RegexOptions.CultureInvariant)]
    private static partial Regex Anaphylaxis();

    [GeneratedRegex(@"\bme\s+matar|\bsuicid|\btirar\s+(a\s+)?(minha\s+)?(propria\s+)?vida|\bnao\s+quero\s+mais\s+viver|\bacabar\s+com\s+(a\s+)?minha\s+vida|\bme\s+machucar\s+de\s+proposito", RegexOptions.CultureInvariant)]
    private static partial Regex SuicideRisk();

    [GeneratedRegex(@"\b(gravida|gestante|gestacao|gravidez)\b.{0,80}\bsang|\bsang\w*\b.{0,80}\b(gravida|gestante|gestacao|gravidez)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PregnancyBleeding();

    [GeneratedRegex(@"\b(pior\s+dor\s+de\s+cabeca|dor\s+de\s+cabeca\s+(muito\s+forte|insuportavel|subita|repentina|explosiva))", RegexOptions.CultureInvariant)]
    private static partial Regex ThunderclapHeadache();

    [GeneratedRegex(@"\bfebre\b.{0,30}\b(39[.,]?[5-9]|4[0-2])\b|\b(39[.,]?[5-9]|4[0-2])\s*(graus|c\b|°)|\bfebre\s+(muito\s+)?alta\s+(ha|faz)\s+(mais\s+de\s+)?([3-9]|tres|quatro|cinco)\s+dias", RegexOptions.CultureInvariant)]
    private static partial Regex HighFever();

    [GeneratedRegex(@"\bdor\s+(muito\s+)?(forte|intensa|insuportavel)\s+(na|no)\s+(barriga|abdomen|estomago)|\b(barriga|abdomen)\s+(dura|muito\s+inchada)", RegexOptions.CultureInvariant)]
    private static partial Regex SevereAbdominalPain();

    [GeneratedRegex(@"\bsangue\s+(nas|na|no)\s+(fezes|coco|urina|xixi)|\b(fezes|coco)\s+(pretas|com\s+sangue)|\burina\s+com\s+sangue", RegexOptions.CultureInvariant)]
    private static partial Regex BloodInStoolOrUrine();

    [GeneratedRegex(@"\b(bati|bateu|batida)\s+(com\s+)?a\s+cabeca|\bpancada\s+na\s+cabeca|\bcai\w*\s+e\s+bati|\btraumatismo", RegexOptions.CultureInvariant)]
    private static partial Regex HeadTrauma();

    [GeneratedRegex(@"\bfebre|\bfebril|\btemperatura\s+(alta|de\s+3[89])", RegexOptions.CultureInvariant)]
    private static partial Regex FeverWord();
}
