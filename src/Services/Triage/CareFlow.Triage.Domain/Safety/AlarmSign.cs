using CareFlow.Triage.Domain.Priorities;

namespace CareFlow.Triage.Domain.Safety;

/// <summary>Sinal de alarme encontrado na queixa e a prioridade mínima que ele impõe.</summary>
public sealed record AlarmSign(string Code, TriagePriority Floor);
