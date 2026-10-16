using System.ComponentModel.DataAnnotations;

namespace CareFlow.Triage.Api.Features.Triages;

internal sealed class TriageOptions
{
    public const string SectionName = "Triage";

    /// <summary>
    /// Triagens novas por clínica por minuto. Cada uma pode virar uma chamada paga ao modelo, então uma clínica
    /// (ou um token vazado) não gasta o orçamento de todas.
    /// </summary>
    [Range(1, 10_000)]
    public int SubmissionsPerClinicPerMinute { get; set; } = 30;
}
