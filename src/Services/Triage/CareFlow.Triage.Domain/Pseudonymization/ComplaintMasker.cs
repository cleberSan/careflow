using System.Text;
using System.Text.RegularExpressions;

using CareFlow.Triage.Domain.Text;

namespace CareFlow.Triage.Domain.Pseudonymization;

/// <summary>
/// Remove identificadores do texto livre antes de ele sair do serviço. As regras procuram formas
/// (e-mail, CPF, telefone, datas, endereços, números longos), nomes que o cofre conhece e nomes apresentados
/// por expressões como "meu nome é", "minha filha" ou "Dra.". Cada trecho encontrado vira um marcador
/// (<c>[nome]</c>, <c>[cpf]</c>...). Sinais clínicos como temperatura, pressão e duração ficam.
/// <para>
/// Limite conhecido: um nome solto, sem expressão que o apresente e que o cofre não conheça (por exemplo
/// "o João da padaria"), passa. Por isso a avaliação tem casos assim e o ADR registra o risco residual.
/// </para>
/// </summary>
public static partial class ComplaintMasker
{
    private const string NamePart = @"\p{Lu}[\p{L}'’-]+";
    private const string NameSequence = NamePart + @"(?:\s+(?:d[aeo]s?\s+|e\s+)?" + NamePart + "){0,3}";

    private static readonly HashSet<string> NameStopWords = new(StringComparer.Ordinal) { "da", "de", "do", "das", "dos", "e" };

    public static MaskedComplaint Mask(string complaint, KnownIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(complaint);
        ArgumentNullException.ThrowIfNull(identity);

        var spans = new List<Span>();
        Collect(spans, EmailPattern(), complaint, MaskKind.Email);
        Collect(spans, LinkPattern(), complaint, MaskKind.Link);
        Collect(spans, HandlePattern(), complaint, MaskKind.Handle);
        Collect(spans, CpfPattern(), complaint, MaskKind.Cpf);
        Collect(spans, PhonePattern(), complaint, MaskKind.Phone);
        Collect(spans, NumericDatePattern(), complaint, MaskKind.Date);
        Collect(spans, WrittenDatePattern(), complaint, MaskKind.Date);
        Collect(spans, DayPattern(), complaint, MaskKind.Date);
        Collect(spans, AgePattern(), complaint, MaskKind.Age, group: "age");
        Collect(spans, AddressPattern(), complaint, MaskKind.Address);
        Collect(spans, LongNumberPattern(), complaint, MaskKind.Number);
        Collect(spans, IntroducedNamePattern(), complaint, MaskKind.Name, group: "name");
        CollectKnownNames(spans, complaint, identity);

        var merged = Merge(spans, complaint);
        var builder = new StringBuilder(complaint.Length);
        var counts = new Dictionary<MaskKind, int>();
        var cursor = 0;
        var maskedChars = 0;
        foreach (var span in merged)
        {
            builder.Append(complaint, cursor, span.Start - cursor);
            builder.Append(span.Kind.Placeholder());
            counts[span.Kind] = counts.GetValueOrDefault(span.Kind) + 1;
            maskedChars += span.Length;
            cursor = span.Start + span.Length;
        }

        builder.Append(complaint, cursor, complaint.Length - cursor);
        var meaningful = complaint.Count(c => !char.IsWhiteSpace(c));
        var share = meaningful == 0 ? 0 : Math.Min(1.0, (double)maskedChars / meaningful);
        return new MaskedComplaint(builder.ToString(), counts, share);
    }

    private static void Collect(List<Span> spans, Regex pattern, string text, MaskKind kind, string? group = null)
    {
        foreach (Match match in pattern.Matches(text))
        {
            var capture = group is null ? match : match.Groups[group];
            if (capture.Success && capture.Length > 0)
            {
                spans.Add(new Span(capture.Index, capture.Length, kind));
            }
        }
    }

    private static void CollectKnownNames(List<Span> spans, string text, KnownIdentity identity)
    {
        var folded = TextFolding.Fold(text);
        foreach (var part in identity.NameParts)
        {
            var name = TextFolding.Fold(part);
            if (name.Length < 3 || NameStopWords.Contains(name))
            {
                continue;
            }

            var index = 0;
            while ((index = folded.IndexOf(name, index, StringComparison.Ordinal)) >= 0)
            {
                var end = index + name.Length;
                if ((index == 0 || !char.IsLetterOrDigit(folded[index - 1])) && (end == folded.Length || !char.IsLetterOrDigit(folded[end])))
                {
                    spans.Add(new Span(index, name.Length, MaskKind.Name));
                }

                index = end;
            }
        }
    }

    /// <summary>
    /// Trechos sobrepostos viram um só, com o tipo do que começou primeiro (ou do maior). Partes de nome separadas
    /// só por espaço também: "[nome] [nome]" diria quantas palavras o nome tem.
    /// </summary>
    private static List<Span> Merge(List<Span> spans, string text)
    {
        var ordered = spans.OrderBy(s => s.Start).ThenByDescending(s => s.Length).ToList();
        var merged = new List<Span>(ordered.Count);
        foreach (var span in ordered)
        {
            if (merged.Count > 0 && (span.Start < merged[^1].Start + merged[^1].Length || AdjacentNames(merged[^1], span, text)))
            {
                var last = merged[^1];
                var end = Math.Max(last.Start + last.Length, span.Start + span.Length);
                merged[^1] = last with { Length = end - last.Start };
                continue;
            }

            merged.Add(span);
        }

        return merged;
    }

    private static bool AdjacentNames(Span previous, Span next, string text)
    {
        if (previous.Kind != MaskKind.Name || next.Kind != MaskKind.Name)
        {
            return false;
        }

        var gapStart = previous.Start + previous.Length;
        return text.AsSpan(gapStart, next.Start - gapStart).IsWhiteSpace();
    }

    private readonly record struct Span(int Start, int Length, MaskKind Kind);

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b(?:https?://|www\.)\S+|\b[\w-]+\.(?:com|net|org|br|io|me)(?:\.br)?(?:/\S*)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"(?<![\w@])@[A-Za-z0-9_.]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();

    [GeneratedRegex(@"(?<!\d)\d{3}[.\s]?\d{3}[.\s]?\d{3}[-.\s]?\d{2}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CpfPattern();

    [GeneratedRegex(@"(?<!\d)(?:\+?\s?55[\s.-]*)?(?:\(?\d{2}\)?[\s.-]*)?9?\d{4}[\s.-]?\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    // 12/03/1980, 12-03-80, 12/03, 1980-03-12, 12.03.1980. "38.5" (temperatura) e "120/80" (pressão) não casam.
    [GeneratedRegex(@"(?<![\d/.-])(?:\d{4}-\d{1,2}-\d{1,2}|\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?|\d{1,2}\.\d{1,2}\.\d{2,4})(?![\d/-])", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDatePattern();

    [GeneratedRegex(@"\b\d{1,2}\s+de\s+(?:janeiro|fevereiro|mar[cç]o|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)(?:\s+de\s+\d{2,4})?\b|\b(?:janeiro|fevereiro|mar[cç]o|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+(?:de\s+)?\d{4}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WrittenDatePattern();

    [GeneratedRegex(@"\bdia\s+\d{1,2}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DayPattern();

    // A idade exata do paciente; a faixa já vai separada. Durações ("há 3 anos") ficam.
    [GeneratedRegex(@"\b(?:tenho|tem|tinha|completei|completo|completou|fiz|fez|com|idade\s+de)\s+(?<age>\d{1,3}\s+anos)\b|\b(?<age>\d{1,3}\s+anos\s+de\s+idade)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AgePattern();

    // Logradouro até a próxima pontuação, mais o número que costuma vir depois da vírgula ("Rua X, 1234"), e
    // números de casa soltos ("nº 12", "número 340").
    [GeneratedRegex(@"\b(?:rua|r\.|avenida|av\.|travessa|tv\.|alameda|al\.|estrada|rodovia|pra[cç]a|largo|condom[ií]nio|bairro|apto\.?|apartamento|bloco|cep)\s+[^,.;:!?\n]{1,60}(?:\s*,\s*(?:n[º°o]\.?\s*)?\d{1,6}[A-Za-z]?\b)?|\b(?:n[º°]|no\.|n[úu]mero)\s*\d{1,6}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    // Seis dígitos ou mais, separados no máximo por ponto, hífen ou espaço: RG, CEP, carteirinha, protocolo.
    [GeneratedRegex(@"(?<![\d])\d(?:[.\s-]?\d){5,}(?![\d])", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumberPattern();

    [GeneratedRegex(
        @"(?i:\b(?:meu\s+nome\s+[eé]|me\s+chamo|chamo-me|aqui\s+[eé]\s+(?:o|a)|(?:eu\s+)?sou\s+(?:o|a)|assinad[oa]|att\.?,?|atenciosamente,?|ass\.?:?)|\b(?:minha|meu)\s+(?:m[aã]e|pai|filh[oa]|espos[oa]|marido|mulher|irm[aã]o?|av[oóô]|net[oa]|ti[oa]|prim[oa]|namorad[oa]|vizinh[oa]|amig[oa]|chefe|m[eé]dic[oa]|enfermeir[oa]|cuidador[a]?)|\b(?:dra?|sra?|srta|doutora?|senhora?|dona|seu|paciente)\.?)\s+(?<name>"
        + NameSequence + ")",
        RegexOptions.CultureInvariant)]
    private static partial Regex IntroducedNamePattern();
}
