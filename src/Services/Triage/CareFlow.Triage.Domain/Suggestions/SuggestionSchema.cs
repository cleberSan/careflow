using System.Text.Json;

using CareFlow.BuildingBlocks.Results;
using CareFlow.Triage.Domain.Priorities;

namespace CareFlow.Triage.Domain.Suggestions;

/// <summary>
/// Contrato da resposta do modelo. O mesmo schema vai ao provedor como saída estruturada e é conferido aqui,
/// sem confiar no provedor: objeto com exatamente <c>priority</c> (um dos quatro nomes) e <c>rationale</c>
/// (uma frase curta, sem quebra de linha). Qualquer coisa fora disso é rejeitada.
/// </summary>
public static class SuggestionSchema
{
    public const int MinRationaleLength = 10;
    public const int MaxRationaleLength = 280;

    /// <summary>Tamanho máximo aceito da resposta bruta; acima disso nem tenta ler.</summary>
    public const int MaxResponseLength = 2048;

    public static readonly Error NotJson = Error.Validation("Triagem.RespostaNaoEJson", "A resposta do modelo não é um JSON válido.");
    public static readonly Error NotAnObject = Error.Validation("Triagem.RespostaNaoEObjeto", "A resposta do modelo não é um objeto.");
    public static readonly Error UnexpectedProperty = Error.Validation("Triagem.RespostaComCampoExtra", "A resposta do modelo tem campos fora do schema.");
    public static readonly Error InvalidPriority = Error.Validation("Triagem.RespostaComPrioridadeInvalida", "A resposta do modelo não traz uma das quatro prioridades.");
    public static readonly Error InvalidRationale = Error.Validation("Triagem.RespostaComJustificativaInvalida", "A justificativa do modelo está vazia, longa demais ou em várias linhas.");

    /// <summary>JSON Schema enviado ao provedor (saída estruturada) e publicado na documentação.</summary>
    public static string JsonSchema { get; } = JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new[] { "priority", "rationale" },
        ["properties"] = new Dictionary<string, object>
        {
            ["priority"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = TriagePriorityNames.All },
            ["rationale"] = new Dictionary<string, object> { ["type"] = "string" },
        },
    });

    public static Result<ModelSuggestion> Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response) || response.Length > MaxResponseLength)
        {
            return NotJson;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException)
        {
            return NotJson;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NotAnObject;
            }

            string? priorityValue = null;
            string? rationale = null;
            var seen = 0;
            foreach (var property in root.EnumerateObject())
            {
                seen++;
                switch (property.Name)
                {
                    case "priority" when property.Value.ValueKind == JsonValueKind.String && priorityValue is null:
                        priorityValue = property.Value.GetString();
                        break;
                    case "rationale" when property.Value.ValueKind == JsonValueKind.String && rationale is null:
                        rationale = property.Value.GetString();
                        break;
                    case "priority" or "rationale":
                        return property.Name == "priority" ? InvalidPriority : InvalidRationale;
                    default:
                        return UnexpectedProperty;
                }
            }

            if (!TriagePriorityNames.TryParse(priorityValue, out var priority))
            {
                return InvalidPriority;
            }

            if (seen != 2 || !IsRationale(rationale))
            {
                return InvalidRationale;
            }

            return new ModelSuggestion(priority, rationale!.Trim());
        }
    }

    private static bool IsRationale(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length is >= MinRationaleLength and <= MaxRationaleLength
            && !trimmed.Any(c => char.IsControl(c));
    }
}
