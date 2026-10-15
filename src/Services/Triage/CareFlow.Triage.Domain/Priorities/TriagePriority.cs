namespace CareFlow.Triage.Domain.Priorities;

/// <summary>
/// Prioridade de atendimento sugerida, da menor para a maior. Os valores numéricos dão a ordem: comparar dois
/// valores diz qual é mais urgente. É ordem de atendimento, não diagnóstico.
/// </summary>
public enum TriagePriority
{
    NonUrgent = 1,
    LessUrgent,
    Urgent,
    Emergency,
}

public static class TriagePriorityNames
{
    private static readonly Dictionary<TriagePriority, string> Names = new()
    {
        [TriagePriority.NonUrgent] = "non-urgent",
        [TriagePriority.LessUrgent] = "less-urgent",
        [TriagePriority.Urgent] = "urgent",
        [TriagePriority.Emergency] = "emergency",
    };

    /// <summary>Nomes estáveis, usados no schema do modelo, na API, nos eventos e na avaliação.</summary>
    public static IReadOnlyCollection<string> All { get; } = [.. Names.Values];

    public static string Slug(this TriagePriority priority) =>
        Names.TryGetValue(priority, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(priority));

    public static bool TryParse(string? value, out TriagePriority priority)
    {
        foreach (var (key, name) in Names)
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                priority = key;
                return true;
            }
        }

        priority = default;
        return false;
    }

    public static TriagePriority Max(TriagePriority a, TriagePriority b) => a >= b ? a : b;
}
