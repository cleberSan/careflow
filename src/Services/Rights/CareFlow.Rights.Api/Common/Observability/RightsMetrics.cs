using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CareFlow.Rights.Api.Common.Observability;

/// <summary>Métricas dos pedidos do titular, por tipo e resultado. Nenhuma tag carrega pseudônimo, pedido ou clínica.</summary>
internal sealed class RightsMetrics
{
    public const string MeterName = "CareFlow.Rights";

    private readonly Counter<long> _received;
    private readonly Counter<long> _finished;
    private readonly Counter<long> _identity;
    private readonly Counter<long> _downloads;
    private readonly Counter<long> _packagesDestroyed;
    private readonly Histogram<double> _duration;

    public RightsMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);
        _received = meter.CreateCounter<long>("careflow.rights.requests.received", "{request}", "Pedidos recebidos.");
        _finished = meter.CreateCounter<long>("careflow.rights.requests.finished", "{request}", "Pedidos concluídos ou negados.");
        _identity = meter.CreateCounter<long>("careflow.rights.identity.checks", "{check}", "Tentativas de confirmar a identidade.");
        _downloads = meter.CreateCounter<long>("careflow.rights.packages.downloaded", "{package}", "Pacotes baixados pelo link.");
        _packagesDestroyed = meter.CreateCounter<long>("careflow.rights.packages.destroyed", "{package}", "Pacotes apagados ao expirar.");
        _duration = meter.CreateHistogram<double>("careflow.rights.requests.duration", "h", "Do recebimento ao fim do pedido, em horas.");
    }

    public void Received(string kind) => _received.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void Finished(string kind, string outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "kind", kind }, { "outcome", outcome } };
        _finished.Add(1, tags);
        _duration.Record(elapsed.TotalHours, tags);
    }

    public void IdentityChecked(string result) => _identity.Add(1, new KeyValuePair<string, object?>("result", result));

    public void Downloaded() => _downloads.Add(1);

    public void PackagesDestroyed(int count)
    {
        if (count > 0)
        {
            _packagesDestroyed.Add(count);
        }
    }
}
