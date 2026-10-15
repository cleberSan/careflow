using System.Diagnostics.Metrics;

namespace CareFlow.Consent.Api.Common.Observability;

/// <summary>Métricas do consentimento, por finalidade. Nenhuma tag carrega pseudônimo ou clínica.</summary>
internal sealed class ConsentMetrics
{
    public const string MeterName = "CareFlow.Consent";

    private readonly Counter<long> _granted;
    private readonly Counter<long> _revoked;

    public ConsentMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);
        _granted = meter.CreateCounter<long>("careflow.consent.granted", "{consent}", "Concessões que mudaram o registro.");
        _revoked = meter.CreateCounter<long>("careflow.consent.revoked", "{consent}", "Revogações que mudaram o registro.");
    }

    public void Granted(string purpose) => _granted.Add(1, new KeyValuePair<string, object?>("purpose", purpose));

    public void Revoked(string purpose) => _revoked.Add(1, new KeyValuePair<string, object?>("purpose", purpose));
}
