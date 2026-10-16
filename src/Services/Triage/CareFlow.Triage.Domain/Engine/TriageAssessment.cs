using CareFlow.Triage.Domain.Priorities;
using CareFlow.Triage.Domain.Pseudonymization;
using CareFlow.Triage.Domain.Safety;

namespace CareFlow.Triage.Domain.Engine;

/// <summary>
/// Resultado da triagem automática, antes da revisão humana. <see cref="Priority"/> nulo quer dizer que nada
/// sustenta uma sugestão (modelo não usado e nenhum alarme): o profissional define.
/// </summary>
public sealed record TriageAssessment(
    TriagePriority? Priority,
    SuggestionSource Source,
    string? Rationale,
    ModelStatus ModelStatus,
    TriagePriority? ModelPriority,
    IReadOnlyList<AlarmSign> AlarmSigns,
    IReadOnlyList<Guardrail> Guardrails,
    MaskedComplaint? Masked,
    ModelPrompt? Prompt,
    string? ModelName);
