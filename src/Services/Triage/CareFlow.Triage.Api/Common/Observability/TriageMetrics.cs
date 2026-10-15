using System.Diagnostics.Metrics;

using CareFlow.Triage.Domain.Cases;
using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.Domain.Priorities;

namespace CareFlow.Triage.Api.Common.Observability;

/// <summary>Métricas da triagem por origem, prioridade, estado do modelo, proteção e revisão. Sem pseudônimo nem clínica.</summary>
internal sealed class TriageMetrics
{
    public const string MeterName = "CareFlow.Triage";

    private readonly Counter<long> _assessed;
    private readonly Counter<long> _guardrails;
    private readonly Counter<long> _reviews;
    private readonly Histogram<double> _modelLatency;

    public TriageMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _assessed = meter.CreateCounter<long>("careflow.triage.assessed", "{triage}", "Triagens avaliadas, por origem da sugestão e estado do modelo.");
        _guardrails = meter.CreateCounter<long>("careflow.triage.guardrails", "{guardrail}", "Proteções acionadas, por tipo e código.");
        _reviews = meter.CreateCounter<long>("careflow.triage.reviews", "{review}", "Revisões humanas, por decisão.");
        _modelLatency = meter.CreateHistogram<double>("careflow.triage.model.duration", "s", "Duração da chamada ao modelo.");
    }

    public void Assessed(TriageAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        _assessed.Add(
            1,
            Tag("source", Slug(assessment.Source)),
            Tag("model_status", assessment.ModelStatus.ToString()),
            Tag("priority", assessment.Priority?.Slug() ?? "none"));
        foreach (var guardrail in assessment.Guardrails)
        {
            _guardrails.Add(1, Tag("kind", guardrail.Kind), Tag("code", guardrail.Code));
        }
    }

    public void Reviewed(ReviewDecision decision, bool changedPriority) =>
        _reviews.Add(1, Tag("decision", decision.ToString()), Tag("changed", changedPriority ? "true" : "false"));

    public void ModelCall(TimeSpan duration, string outcome) => _modelLatency.Record(duration.TotalSeconds, Tag("outcome", outcome));

    public static string Slug(SuggestionSource source) => source switch
    {
        SuggestionSource.Model => "model",
        SuggestionSource.ModelRaisedByRules => "model-raised",
        SuggestionSource.Rules => "rules",
        _ => "none",
    };

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);
}
