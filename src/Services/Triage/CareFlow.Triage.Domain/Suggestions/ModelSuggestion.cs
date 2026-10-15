using CareFlow.Triage.Domain.Priorities;

namespace CareFlow.Triage.Domain.Suggestions;

/// <summary>Resposta do modelo já validada contra o schema.</summary>
public sealed record ModelSuggestion(TriagePriority Priority, string Rationale);
