using System.Diagnostics;

using OpenTelemetry;

namespace CareFlow.BuildingBlocks.AspNetCore.Observability;

/// <summary>
/// Descarta spans de banco que não têm pai: são as consultas de varredura dos serviços de fundo (Outbox, fila de
/// avisos, medição do backlog), que rodam a cada poucos milissegundos sem nenhuma operação por trás. Exportadas,
/// elas viram milhares de traces de um span só e escondem os que importam. Consultas feitas dentro de uma
/// requisição, de um consumo ou de um envio têm pai e continuam no trace.
/// </summary>
public sealed class UnparentedQueryFilter : BaseProcessor<Activity>
{
    public const string NpgsqlSource = "Npgsql";

    public override void OnStart(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Parent is null && data.ParentSpanId == default && data.Source.Name == NpgsqlSource)
        {
            // Os exportadores ignoram spans sem a marca de gravado.
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
