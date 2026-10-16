using System.Diagnostics;

namespace CareFlow.Triage.Api.Common.Observability;

/// <summary>
/// Spans da triagem: um para a avaliação e um para a chamada ao modelo. Tags só com prioridade, origem,
/// estado do modelo e códigos de proteção; nunca a queixa, o texto enviado, o pseudônimo ou a clínica.
/// </summary>
internal static class TriageDiagnostics
{
    public const string SourceName = "CareFlow.Triage";

    public static readonly ActivitySource ActivitySource = new(SourceName);
}
