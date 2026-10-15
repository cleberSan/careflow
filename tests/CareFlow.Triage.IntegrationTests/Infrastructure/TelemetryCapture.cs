using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CareFlow.Triage.IntegrationTests.Infrastructure;

/// <summary>
/// Escuta todos os spans e as medições do processo de teste (não só os do CareFlow: HttpClient, Npgsql e ASP.NET
/// Core também), para provar que nenhum dado pessoal sai pela telemetria.
/// </summary>
internal sealed class TelemetryCapture : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();

    public TelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Spans.Enqueue(Describe(activity)),
        };
        ActivitySource.AddActivityListener(_activities);

        _meters.InstrumentPublished = (instrument, listener) => listener.EnableMeasurementEvents(instrument);
        _meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Measurements.Enqueue(Describe(instrument, tags)));
        _meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Measurements.Enqueue(Describe(instrument, tags)));
        _meters.SetMeasurementEventCallback<int>((instrument, _, tags, _) => Measurements.Enqueue(Describe(instrument, tags)));
        _meters.Start();
    }

    /// <summary>Cada span como texto: nome, status, tags, eventos e links.</summary>
    public ConcurrentQueue<string> Spans { get; } = new();

    public ConcurrentQueue<string> Measurements { get; } = new();

    public IEnumerable<string> All => Spans.Concat(Measurements);

    public void Clear()
    {
        Spans.Clear();
        Measurements.Clear();
    }

    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }

    private static string Describe(Activity activity) =>
        string.Join(
            '|',
            new[] { activity.Source.Name, activity.DisplayName, activity.StatusDescription ?? string.Empty }
                .Concat(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))
                .Concat(activity.Events.SelectMany(e => e.Tags.Select(tag => $"{e.Name}:{tag.Key}={tag.Value}")))
                .Concat(activity.Baggage.Select(item => $"baggage:{item.Key}={item.Value}")));

    private static string Describe(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var parts = new List<string> { instrument.Meter.Name, instrument.Name };
        foreach (var tag in tags)
        {
            parts.Add($"{tag.Key}={tag.Value}");
        }

        return string.Join('|', parts);
    }
}
