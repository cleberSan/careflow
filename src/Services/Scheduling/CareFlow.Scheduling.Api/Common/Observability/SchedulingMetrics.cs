using System.Diagnostics.Metrics;

namespace CareFlow.Scheduling.Api.Common.Observability;

/// <summary>Métricas de negócio da agenda. Nenhuma tag carrega dado pessoal.</summary>
internal sealed class SchedulingMetrics
{
    public const string MeterName = "CareFlow.Scheduling";

    private readonly Counter<long> _booked;
    private readonly Counter<long> _conflicts;
    private readonly Counter<long> _replays;
    private readonly Counter<long> _cancelled;

    public SchedulingMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);
        _booked = meter.CreateCounter<long>("careflow.scheduling.appointments.booked", "{appointment}", "Consultas reservadas.");
        _conflicts = meter.CreateCounter<long>("careflow.scheduling.appointments.conflicts", "{request}", "Reservas recusadas por horário já ocupado.");
        _replays = meter.CreateCounter<long>("careflow.scheduling.idempotent_replays", "{request}", "Requisições repetidas atendidas pela chave de idempotência.");
        _cancelled = meter.CreateCounter<long>("careflow.scheduling.appointments.cancelled", "{appointment}", "Consultas canceladas.");
    }

    public void Booked() => _booked.Add(1);

    public void Conflict() => _conflicts.Add(1);

    public void Replayed() => _replays.Add(1);

    public void Cancelled() => _cancelled.Add(1);
}
