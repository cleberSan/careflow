namespace CareFlow.Triage.Domain.Cases;

/// <summary>O que o profissional fez com a sugestão.</summary>
public enum ReviewDecision
{
    /// <summary>Ainda não revisada: a sugestão não vale.</summary>
    Pending = 0,
    Confirmed,
    Corrected,
}
