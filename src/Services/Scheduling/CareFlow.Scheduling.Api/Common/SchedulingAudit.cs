namespace CareFlow.Scheduling.Api.Common;

/// <summary>Como a agenda aparece na trilha de auditoria. O recurso é o paciente: a trilha responde "quem mexeu neste paciente?".</summary>
internal static class SchedulingAudit
{
    public const string SourceService = "scheduling";
    public const string ResourceType = "patient";

    /// <summary>Ator das ações automáticas da agenda (cancelamento por revogação).</summary>
    public const string SystemActor = "careflow-scheduling";
}
