using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Test doubles that capture the three telemetry signals a <see cref="DeltaSharp.Storage.Delta.DeltaCommitter"/>
/// emits — structured logs (<see cref="RecordingLogger{T}"/>), metric measurements
/// (<see cref="MeterCapture"/>), and trace spans (<see cref="ActivityCapture"/>) — so a test can assert log
/// fields, metric labels, and span attributes on a representative commit scenario. Each capture is scoped to
/// a specific <see cref="Meter"/>/<see cref="ActivitySource"/> instance (by reference identity), so parallel
/// test classes that each build their own telemetry surface never observe one another's signals despite the
/// shared meter/source <b>names</b>.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    internal sealed record Entry(
        LogLevel Level, EventId EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)
    {
        internal object? Field(string name)
        {
            foreach (KeyValuePair<string, object?> kvp in State)
            {
                if (string.Equals(kvp.Key, name, StringComparison.Ordinal))
                {
                    return kvp.Value;
                }
            }

            return null;
        }
    }

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly List<IReadOnlyList<KeyValuePair<string, object?>>> _scopes = new();

    internal IReadOnlyList<Entry> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    internal IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Scopes
    {
        get { lock (_gate) { return _scopes.ToArray(); } }
    }

    internal Entry Single(string eventName) => Entries.Single(e => e.EventId.Name == eventName);

    internal bool Has(string eventName) => Entries.Any(e => e.EventId.Name == eventName);

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> kvps)
        {
            lock (_gate)
            {
                _scopes.Add(kvps);
            }
        }

        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var fields = state as IReadOnlyList<KeyValuePair<string, object?>> ?? Array.Empty<KeyValuePair<string, object?>>();
        var entry = new Entry(logLevel, eventId, formatter(state, exception), fields);
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>Captures metric measurements from a specific set of <see cref="Meter"/> instances via a scoped
/// <see cref="MeterListener"/>, recording each measurement's instrument name, value, and tags.</summary>
internal sealed class MeterCapture : IDisposable
{
    internal sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private readonly object _gate = new();
    private readonly List<Measurement> _measurements = new();
    private readonly MeterListener _listener = new();

    internal MeterCapture(params Meter[] meters)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (Array.IndexOf(meters, instrument.Meter) >= 0)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((inst, value, tags, _) => Record(inst.Name, value, tags));
        _listener.SetMeasurementEventCallback<int>((inst, value, tags, _) => Record(inst.Name, value, tags));
        _listener.SetMeasurementEventCallback<double>((inst, value, tags, _) => Record(inst.Name, value, tags));
        _listener.Start();
    }

    internal IReadOnlyList<Measurement> Measurements
    {
        get { lock (_gate) { return _measurements.ToArray(); } }
    }

    internal IEnumerable<Measurement> ForInstrument(string name) =>
        Measurements.Where(m => string.Equals(m.Instrument, name, StringComparison.Ordinal));

    private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            map[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new Measurement(instrument, value, map));
        }
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>Captures stopped <see cref="Activity"/> spans from a specific <see cref="ActivitySource"/> via a
/// scoped <see cref="ActivityListener"/> that samples all data so span attributes are recorded.</summary>
internal sealed class ActivityCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    internal ActivityCapture(ActivitySource source)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_gate)
                {
                    _stopped.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    internal IReadOnlyList<Activity> Stopped
    {
        get { lock (_gate) { return _stopped.ToArray(); } }
    }

    public void Dispose() => _listener.Dispose();
}
