using System.Text.Json;

using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.Domain.Text;

namespace CareFlow.Triage.Api.Models;

/// <summary>
/// Modelo falso e determinístico, para desenvolvimento e testes: lê a queixa pseudonimizada e decide por
/// palavras de intensidade, de propósito ingênuo. Não conhece sinais de alarme ("dor no peito leve" sai
/// não urgente), o que deixa visível a regra determinística subindo a prioridade. Nunca é usado para medir
/// a qualidade da triagem: a avaliação publicada usa respostas gravadas de um modelo real.
/// </summary>
internal sealed class DeterministicTriageModel : ITriageModel
{
    public const string ModelName = "fake-deterministic-v1";

    private static readonly string[] Strong = ["forte", "intensa", "insuportavel", "muito", "piorando", "piorou", "nao passa"];
    private static readonly string[] Moderate = ["febre", "dor", "vomit", "diarreia", "tontura", "inchac"];

    public string Name => ModelName;

    public Task<ModelReply> CompleteAsync(ModelPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        using var payload = JsonDocument.Parse(prompt.Payload);
        var complaint = TextFolding.Fold(payload.RootElement.GetProperty("queixa").GetString() ?? string.Empty);

        var (priority, rationale) = Strong.Any(complaint.Contains) && Moderate.Any(complaint.Contains)
            ? ("urgent", "Sintoma descrito como intenso ou piorando no relato.")
            : Moderate.Any(complaint.Contains)
                ? ("less-urgent", "Sintomas relatados sem intensidade forte descrita.")
                : ("non-urgent", "Queixa sem sintoma intenso descrito no relato.");

        var json = JsonSerializer.Serialize(new { priority, rationale });
        return Task.FromResult<ModelReply>(new ModelReply.Completed(json));
    }
}
