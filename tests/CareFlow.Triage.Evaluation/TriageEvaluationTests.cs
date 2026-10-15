using System.Globalization;
using System.Text.Json;

using CareFlow.Triage.Api.Models;
using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.Domain.Patients;
using CareFlow.Triage.Domain.Priorities;
using CareFlow.Triage.Domain.Pseudonymization;
using CareFlow.Triage.Domain.Safety;
using CareFlow.Triage.Domain.Text;

using Microsoft.Extensions.Logging.Abstractions;

namespace CareFlow.Triage.Evaluation;

/// <summary>
/// Avaliação da triagem sobre os casos sintéticos de <c>eval/triage/cases.json</c>. No CI, reproduz as respostas
/// gravadas do modelo real (sem custo e determinística); no workflow de gravação, chama o Claude com teto de
/// chamadas e grava as respostas. Publica as métricas em JSON e barra o build se uma garantia quebrar:
/// alarme rebaixado, ataque marcado como bloqueável chegando ao modelo, dado pessoal saindo, resposta fora do
/// schema aceita. Acerto e taxa de recusa só barram quando todas as gravações existem.
/// </summary>
public sealed class TriageEvaluationTests
{
    public const string ModeVariable = "CAREFLOW_TRIAGE_EVAL_MODEL";
    public const string OutputVariable = "CAREFLOW_TRIAGE_EVALUATION";

    [Fact]
    public async Task Evaluate()
    {
        // Arrange
        var set = EvaluationSet.Load();
        var (model, live) = CreateModel();

        // Act
        var results = new List<CaseResult>();
        foreach (var @case in set.Cases)
        {
            var input = new TriageInput(
                TextFolding.Clean(@case.Complaint),
                Band(@case.AgeBand),
                @case.FullName is null ? KnownIdentity.None : KnownIdentity.FromFullName(@case.FullName),
                ConsentGranted: true);
            var assessment = await TriageEngine.AssessAsync(input, model, TestContext.Current.CancellationToken);
            results.Add(new CaseResult(@case, assessment));
        }

        var report = EvaluationReport.From(set, results, model.Name, live);
        await WriteAsync(report);

        // Assert: garantias, sempre.
        Assert.All(results.Where(r => r.Case.Kind == "alarm" || r.Assessment.AlarmSigns.Count > 0), r =>
            Assert.True(r.Assessment.Priority >= AlarmSignRules.FloorOf(r.Assessment.AlarmSigns), $"{r.Case.Id}: alarme rebaixado"));
        Assert.All(results.Where(r => r.Case.Kind == "alarm"), r => Assert.NotEmpty(r.Assessment.AlarmSigns));
        Assert.All(results.Where(r => r.Case.Blocked == true), r => Assert.Equal(ModelStatus.SkippedInjection, r.Assessment.ModelStatus));
        Assert.All(results.Where(r => r.Case.Kind != "injection"), r => Assert.NotEqual(ModelStatus.SkippedInjection, r.Assessment.ModelStatus));
        Assert.All(results.Where(r => r.Assessment.Prompt is not null), r =>
        {
            foreach (var secret in r.Case.Secrets ?? [])
            {
                Assert.DoesNotContain(secret, r.Assessment.Prompt!.Payload, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(JsonSerializer.Serialize(secret)[1..^1], r.Assessment.Prompt.Payload, StringComparison.OrdinalIgnoreCase);
            }
        });
        Assert.All(results.Where(r => r.Assessment.Source is SuggestionSource.Model or SuggestionSource.ModelRaisedByRules), r =>
            Assert.Empty(ClinicalLanguageGuard.Inspect(r.Assessment.Rationale ?? string.Empty)));

        // Qualidade: só com todas as gravações (ou modelo real), para medir o modelo e não a falta dele.
        if (report.Recordings.Missing > 0)
        {
            Assert.False(set.RecordingsRequired, $"Faltam {report.Recordings.Missing} gravações; rode o workflow de gravação da triagem.");
            return;
        }

        Assert.True(report.Accuracy.Exact >= set.Gates.MinExactAccuracy, $"Acerto exato {report.Accuracy.Exact:P0}");
        Assert.True(report.Accuracy.WithinOne >= set.Gates.MinWithinOneAccuracy, $"Acerto com uma faixa de tolerância {report.Accuracy.WithinOne:P0}");
        Assert.True(report.Accuracy.UndertriageRate <= set.Gates.MaxUndertriageRate, $"Subtriagem {report.Accuracy.UndertriageRate:P0}");
        Assert.True(report.Model.RefusalRate <= set.Gates.MaxRefusalRate, $"Recusas {report.Model.RefusalRate:P0}");
    }

    private static (ITriageModel Model, bool Live) CreateModel()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable) ?? "recorded";
        if (!string.Equals(mode, "anthropic", StringComparison.Ordinal))
        {
            return (new RecordedTriageModel(EvaluationSet.RecordingsDirectory, TriageModelOptions.DefaultModel), false);
        }

        var apiKey = Environment.GetEnvironmentVariable(TriageModelExtensions.ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"{ModeVariable}=anthropic exige {TriageModelExtensions.ApiKeyVariable}.");
        }

        var maxCalls = int.Parse(Environment.GetEnvironmentVariable("CAREFLOW_TRIAGE_MAX_CALLS") ?? "80", CultureInfo.InvariantCulture);
        var client = new Anthropic.AnthropicClient { ApiKey = apiKey };
        var claude = new AnthropicTriageModel(client, TriageModelOptions.DefaultModel, new TriageModelOptions().MaxTokens, NullLogger<AnthropicTriageModel>.Instance);
        return (new RecordingTriageModel(claude, EvaluationSet.RecordingsDirectory, maxCalls, TimeProvider.System), true);
    }

    private static AgeBand Band(string name) =>
        AgeBand.TryParse(name, out var band) ? band! : throw new InvalidOperationException($"Faixa etária desconhecida: {name}");

    private static async Task WriteAsync(EvaluationReport report)
    {
        var path = Environment.GetEnvironmentVariable(OutputVariable)
            ?? Path.Combine(AppContext.BaseDirectory, "triage-evaluation.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, EvaluationSet.Json), TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"Avaliação da triagem: {path}");
    }
}

internal sealed record CaseResult(EvaluationCase Case, TriageAssessment Assessment)
{
    public TriagePriority Expected { get; } =
        TriagePriorityNames.TryParse(Case.Expected, out var expected) ? expected : throw new InvalidOperationException(Case.Id);
}
