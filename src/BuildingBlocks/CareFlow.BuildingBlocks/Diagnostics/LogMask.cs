namespace CareFlow.BuildingBlocks.Diagnostics;

/// <summary>
/// Forma mascarada de um pseudônimo para logs: os quatro primeiros dígitos, o bastante para alguém de plantão
/// cruzar duas linhas do mesmo incidente, e pouco demais para identificar o paciente ou buscá-lo em outro serviço.
/// </summary>
public static class LogMask
{
    public static string Pseudonym(Guid value) => value == Guid.Empty ? "(vazio)" : string.Concat(value.ToString("N").AsSpan(0, 4), "…");
}
