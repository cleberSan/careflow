using System.Text.Json;
using System.Text.Json.Serialization;

using CareFlow.Triage.Domain.Engine;

namespace CareFlow.Triage.Api.Models;

/// <summary>
/// Resposta real de um modelo, gravada para ser reproduzida sem custo (CI, avaliação e Painel). O nome do
/// arquivo é o <see cref="ModelPrompt.Digest"/> do pedido: se instruções, schema ou queixa mudarem, a gravação
/// antiga deixa de casar e precisa ser refeita. <see cref="Payload"/> repete o caso pseudonimizado enviado, só
/// para quem revisa a gravação no PR ver o que saiu; a reprodução usa apenas o digest.
/// </summary>
internal sealed record ModelCassette(
    string Digest,
    string PromptVersion,
    string Model,
    DateTimeOffset RecordedAt,
    string Outcome,
    string? Text,
    string? Payload = null)
{
    public const string CompletedOutcome = "completed";
    public const string RefusedOutcome = "refused";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string FileName(string digest) => digest + ".json";

    public ModelReply ToReply() => Outcome switch
    {
        CompletedOutcome => new ModelReply.Completed(Text ?? string.Empty),
        RefusedOutcome => new ModelReply.Refused(),
        _ => new ModelReply.Unavailable("gravacao-invalida"),
    };

    public static ModelCassette? FromReply(ModelPrompt prompt, string model, DateTimeOffset now, ModelReply reply) => reply switch
    {
        ModelReply.Completed completed => new ModelCassette(prompt.Digest, prompt.Version, model, now, CompletedOutcome, completed.Text, prompt.Payload),
        ModelReply.Refused => new ModelCassette(prompt.Digest, prompt.Version, model, now, RefusedOutcome, null, prompt.Payload),
        _ => null,
    };
}
