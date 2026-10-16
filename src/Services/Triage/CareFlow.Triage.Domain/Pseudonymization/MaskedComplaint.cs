namespace CareFlow.Triage.Domain.Pseudonymization;

/// <summary>
/// Queixa pseudonimizada: identificadores trocados por marcadores. <see cref="MaskedShare"/> é a fração do
/// texto original coberta por marcadores; perto de 1, sobra pouco além de identificadores e a queixa não vai
/// ao modelo.
/// </summary>
public sealed record MaskedComplaint(string Text, IReadOnlyDictionary<MaskKind, int> Counts, double MaskedShare)
{
    public int Total => Counts.Values.Sum();
}
