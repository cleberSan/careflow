using System.ComponentModel.DataAnnotations;

using CareFlow.Scheduling.Domain.Appointments;

namespace CareFlow.Scheduling.Api.Infrastructure;

internal sealed class SchedulingOptions
{
    public const string SectionName = "Scheduling";

    [Range(1, 1440)]
    public int MinimumDurationMinutes { get; init; } = 10;

    [Range(1, 1440)]
    public int MaximumDurationMinutes { get; init; } = 240;

    /// <summary>Maior janela aceita na listagem da agenda, para limitar o volume de cada consulta ao banco.</summary>
    [Range(1, 92)]
    public int MaximumListRangeDays { get; init; } = 31;

    public SchedulingPolicy ToPolicy() =>
        new(TimeSpan.FromMinutes(MinimumDurationMinutes), TimeSpan.FromMinutes(MaximumDurationMinutes));
}
