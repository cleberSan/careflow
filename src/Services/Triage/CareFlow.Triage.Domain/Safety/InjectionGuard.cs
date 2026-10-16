using System.Text.RegularExpressions;

using CareFlow.Triage.Domain.Text;

namespace CareFlow.Triage.Domain.Safety;

/// <summary>
/// Procura, na queixa, texto que tenta dar ordens ao modelo em vez de descrever sintomas: pedidos para
/// ignorar instruções, troca de papel, marcação de prompt (tags, cercas de código, "system:"), pedidos de
/// prioridade ou de formato da resposta. Encontrou um sinal, a queixa não vai ao modelo: a triagem fica com as
/// regras locais e com o profissional. É a primeira de quatro camadas; as outras são a queixa ir como dado
/// (string JSON) com instruções fixas, a saída validada contra o schema e o piso das regras de alarme.
/// </summary>
public static partial class InjectionGuard
{
    private static readonly (string Code, Regex Pattern)[] Signals =
    [
        ("ignorar-instrucoes", IgnoreInstructions()),
        ("troca-de-papel", RoleChange()),
        ("marcacao-de-prompt", PromptMarkup()),
        ("pedido-de-prioridade", PriorityRequest()),
        ("pedido-de-formato", FormatRequest()),
        ("vazar-instrucoes", LeakRequest()),
    ];

    public static IReadOnlyList<string> Codes { get; } = [.. Signals.Select(s => s.Code)];

    public static IReadOnlyList<string> Inspect(string complaint)
    {
        ArgumentNullException.ThrowIfNull(complaint);
        var folded = TextFolding.Fold(complaint);
        return [.. Signals.Where(s => s.Pattern.IsMatch(folded)).Select(s => s.Code)];
    }

    [GeneratedRegex(@"\b(ignor\w*|desconsider\w*|esquec\w*|desobedec\w*|anul\w*|override|disregard|forget|bypass)\b.{0,40}\b(instruc\w*|regras?|orientac\w*|comandos?|prompt|diretrizes|acima|anteriores?|previous|above|instructions?|rules)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IgnoreInstructions();

    [GeneratedRegex(@"\b(voce\s+(agora\s+)?(e|sera|vai\s+ser|passa\s+a\s+ser)\s+(um|uma|o|a)\b|a\s+partir\s+de\s+agora\s+voce|aja\s+como|finja\s+(ser|que)|fa(z|ca)\s+de\s+conta|you\s+are\s+now|act\s+as|pretend\s+(to\s+be|you)|modo\s+(desenvolvedor|developer|dan|jailbreak)|jailbreak)", RegexOptions.CultureInvariant)]
    private static partial Regex RoleChange();

    [GeneratedRegex(@"(</?\s*[a-z_]+\s*>|```|\[/?(inst|system|sys)\]|^\s*#{2,}|\b(system|assistant|sistema|assistente|developer|usuario|user)\s*:|\{\s*""|""\s*:\s*""|\bprompt\b)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex PromptMarkup();

    [GeneratedRegex(@"\b(classifi\w*|marqu\w*|defin\w*|coloqu\w*|atribu\w*|retorn\w*|respond\w*|devolv\w*|sugir\w*|escrev\w*)\b.{0,40}\b(prioridade|priority|como\s+(nao[\s-]urgente|pouco\s+urgente|urgente|emergencia)|non-urgent|less-urgent|emergency)\b|\bprioridade\s+(deve|tem\s+que|precisa)\s+ser", RegexOptions.CultureInvariant)]
    private static partial Regex PriorityRequest();

    [GeneratedRegex(@"\b(responda|responder|escreva|retorne|output|reply|answer)\b.{0,30}\b(json|apenas|somente|so\s+com|only|exatamente|formato|ingles|english)\b|\brationale\b|\bjustificativa\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex FormatRequest();

    [GeneratedRegex(@"\b(mostr\w*|revel\w*|repit\w*|imprim\w*|print|reveal|show|repeat)\b.{0,40}\b(instruc\w*|prompt|regras|configurac\w*|instructions|system)\b", RegexOptions.CultureInvariant)]
    private static partial Regex LeakRequest();
}
