using CareFlow.Triage.Domain.Cases;
using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.Domain.Priorities;

namespace CareFlow.Triage.Api.Features.Triages;

/// <summary>Queixa do paciente em texto livre. Só dados sintéticos na demonstração.</summary>
public sealed record SubmitTriageRequest(Guid PatientRef, string? Complaint)
{
    public override string ToString() => $"SubmitTriageRequest {{ Complaint = *** }}";
}

/// <summary><c>confirm</c> mantém a sugestão; <c>correct</c> exige <see cref="Priority"/> diferente da sugerida.</summary>
public sealed record ReviewTriageRequest(string? Decision, string? Priority);

/// <summary>
/// Uma triagem completa, para quem revisa e para o Painel: o que o paciente escreveu, o que saiu para o modelo
/// depois da pseudonimização (e o resumo SHA-256 que está na trilha), a sugestão, as proteções acionadas e a
/// revisão humana. Prioridades e estados vão como nomes estáveis (<c>urgent</c>, <c>rejected-schema</c>).
/// </summary>
public sealed record TriageResponse(
    Guid Id,
    Guid PatientRef,
    string Complaint,
    string AgeBand,
    string? SentToModel,
    string? PromptDigest,
    string? PromptVersion,
    string? ModelName,
    string ModelStatus,
    string? ModelPriority,
    string? SuggestedPriority,
    string Source,
    string? Rationale,
    IReadOnlyList<string> Guardrails,
    IReadOnlyList<string> AlarmSigns,
    IReadOnlyDictionary<string, int> Masks,
    DateTimeOffset CreatedAt,
    string Decision,
    string? FinalPriority,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt)
{
    public override string ToString() => $"TriageResponse {{ Id = {Id}, *** }}";

    public static TriageResponse From(TriageCase triage)
    {
        ArgumentNullException.ThrowIfNull(triage);
        return new TriageResponse(
            triage.Id,
            triage.PatientRef,
            triage.Complaint,
            triage.AgeBand,
            triage.SentToModel,
            triage.PromptDigest,
            triage.PromptVersion,
            triage.ModelName,
            Kebab(triage.ModelStatus.ToString()),
            triage.ModelPriority?.Slug(),
            triage.SuggestedPriority?.Slug(),
            SourceSlug(triage.Source),
            triage.Rationale,
            triage.Guardrails,
            triage.AlarmSigns,
            triage.Masks,
            triage.CreatedAt,
            triage.Decision.ToString().ToLowerInvariant(),
            triage.FinalPriority?.Slug(),
            triage.ReviewedBy,
            triage.ReviewedAt);
    }

    public static string SourceSlug(SuggestionSource source) => source switch
    {
        SuggestionSource.Model => "model",
        SuggestionSource.ModelRaisedByRules => "model-raised-by-rules",
        SuggestionSource.Rules => "rules",
        _ => "none",
    };

    // RejectedSchema -> rejected-schema
    private static string Kebab(string value) =>
        string.Concat(value.Select((c, i) => char.IsUpper(c) ? (i == 0 ? "" : "-") + char.ToLowerInvariant(c) : c.ToString()));
}
