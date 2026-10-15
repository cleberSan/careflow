using System.Globalization;
using System.Text;

namespace CareFlow.Triage.Domain.Text;

/// <summary>
/// Normalização usada pelas regras: minúsculas e sem acento, caractere por caractere, então o texto dobrado
/// tem o mesmo tamanho do original e um trecho encontrado nele aponta para o mesmo trecho do original.
/// </summary>
public static class TextFolding
{
    public static string Fold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = FoldChar(source[i]);
            }
        });
    }

    /// <summary>
    /// Limpa o texto recebido antes de qualquer regra: forma NFC, sem caracteres de controle ou invisíveis
    /// (usados para esconder instruções), espaços seguidos viram um.
    /// </summary>
    public static string Clean(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var lastWasSpace = false;
        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || category is UnicodeCategory.Control)
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            if (category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    private static char FoldChar(char c)
    {
        if (c < 128)
        {
            return char.ToLowerInvariant(c);
        }

        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        return decomposed.Length > 0 && decomposed[0] < 128 ? char.ToLowerInvariant(decomposed[0]) : char.ToLowerInvariant(c);
    }
}
