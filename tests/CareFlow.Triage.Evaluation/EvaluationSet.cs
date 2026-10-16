using System.Text.Json;
using System.Text.Json.Serialization;

namespace CareFlow.Triage.Evaluation;

/// <summary>O conjunto de casos sintéticos em <c>eval/triage/cases.json</c>, com os limites que o CI exige.</summary>
internal sealed record EvaluationSet(string Description, bool RecordingsRequired, EvaluationGates Gates, IReadOnlyList<EvaluationCase> Cases)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Root { get; } = FindRoot();

    public static string Directory => Path.Combine(Root, "eval", "triage");

    public static string RecordingsDirectory => Path.Combine(Directory, "recordings");

    public static EvaluationSet Load() =>
        JsonSerializer.Deserialize<EvaluationSet>(File.ReadAllText(Path.Combine(Directory, "cases.json")), Json)
        ?? throw new InvalidOperationException("cases.json vazio.");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CareFlow.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Raiz do repositório (CareFlow.slnx) não encontrada.");
    }
}

internal sealed record EvaluationGates(double MinExactAccuracy, double MinWithinOneAccuracy, double MaxUndertriageRate, double MaxRefusalRate);

/// <summary>
/// Um caso: <c>kind</c> é <c>clinical</c>, <c>alarm</c>, <c>injection</c> ou <c>personal-data</c>. Ataques com
/// <c>blocked</c> verdadeiro precisam ser barrados antes do modelo; os outros são ataques que escapam do detector e
/// medem as camadas seguintes. <c>secrets</c> são valores que nunca podem sair do serviço.
/// </summary>
internal sealed record EvaluationCase(
    string Id,
    string Kind,
    string AgeBand,
    string Complaint,
    string Expected,
    string? FullName = null,
    bool? Blocked = null,
    IReadOnlyList<string>? Secrets = null)
{
    public override string ToString() => Id;
}
