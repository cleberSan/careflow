using System.Diagnostics.Metrics;

namespace CareFlow.Audit.Api.Common.Observability;

/// <summary>Métricas da auditoria. Nenhuma tag carrega pseudônimo, ator ou clínica.</summary>
internal sealed class AuditMetrics
{
    public const string MeterName = "CareFlow.Audit";

    private readonly Counter<long> _appended;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _verifications;

    public AuditMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);
        _appended = meter.CreateCounter<long>("careflow.audit.entries.appended", "{entry}", "Entradas gravadas na cadeia.");
        _duplicates = meter.CreateCounter<long>("careflow.audit.entries.duplicates", "{event}", "Eventos já registrados, ignorados.");
        _verifications = meter.CreateCounter<long>("careflow.audit.chain.verifications", "{verification}", "Verificações da cadeia, por resultado.");
    }

    public void Appended(string action) => _appended.Add(1, new KeyValuePair<string, object?>("action", action));

    public void Duplicate() => _duplicates.Add(1);

    public void Verified(bool intact) =>
        _verifications.Add(1, new KeyValuePair<string, object?>("result", intact ? "intact" : "broken"));
}
