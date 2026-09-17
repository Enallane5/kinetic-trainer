using KineticTrainer.Core.Rides;
using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Telemetry;

namespace KineticTrainer.Recording;

/// <summary>A finished ride: what was recorded plus its statistics.</summary>
public sealed record RideRecording(
    DateTimeOffset StartedAt,
    DateTimeOffset StoppedAt,
    IReadOnlyList<RideSample> Samples,
    RideSummary Summary,
    string? SensorName,
    bool Simulated)
{
    /// <summary>Optional name/label (e.g. the workout name).</summary>
    public string? Title { get; init; }

    /// <summary>The structured workout ridden, as ZWO XML (null for a free ride).</summary>
    public string? WorkoutZwo { get; init; }

    /// <summary>Workout compliance summary as JSON (owned by the Workouts layer; stored opaquely).</summary>
    public string? ComplianceJson { get; init; }
}

/// <summary>
/// Records timestamped samples from the telemetry stream between Start and Stop. Independent of the UI and of the
/// sensor connection: the ride timer is wall-clock time, so a drop-out leaves a gap in the samples but does not stop
/// the ride, and no values are ever invented for the gap. Values are normalised once here so statistics, FIT and TCX
/// agree: negative power (braking/coasting trainers) is recorded as 0 W, cadence is clamped to 0-254 rpm, and
/// non-finite numbers are dropped.
/// </summary>
public sealed class RideRecorder : IDisposable
{
    /// <summary>Distance is only integrated across consecutive samples closer together than this.</summary>
    public const double MaxIntegrationGapSeconds = RideStatisticsCalculator.MaxIntegrationGapSeconds;

    public const double MaxCadenceRpm = 254;

    private readonly Core.Streaming.Broadcast<TrainerTelemetry>.Subscription _subscription;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<RideSample> _samples = [];
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastSampleAt;
    private double _distance;
    private double _powerSum;
    private int _powerCount;
    private string? _sensorName;
    private bool _simulated;
    private int? _ftp;
    private TrainerTelemetry? _latest;

    public RideRecorder(ITelemetryStream stream)
    {
        // Subscribe synchronously so nothing published after construction can be missed.
        _subscription = stream.Subscribe("recorder", 256);
        _ = Task.Run(() => PumpAsync(_cts.Token));
    }

    public bool IsRecording
    {
        get { lock (_gate) return _startedAt is not null; }
    }

    public DateTimeOffset? StartedAt
    {
        get { lock (_gate) return _startedAt; }
    }

    public TimeSpan Elapsed
    {
        get { lock (_gate) return _startedAt is { } s ? DateTimeOffset.UtcNow - s : TimeSpan.Zero; }
    }

    public double DistanceMeters
    {
        get { lock (_gate) return _distance; }
    }

    public int SampleCount
    {
        get { lock (_gate) return _samples.Count; }
    }

    /// <summary>Running mean of recorded power samples (approximate; the final summary uses the 1 Hz series).</summary>
    public double? AveragePowerWatts
    {
        get { lock (_gate) return _powerCount == 0 ? null : _powerSum / _powerCount; }
    }

    /// <summary>Copy of the samples recorded so far (for live charts / workout compliance).</summary>
    public IReadOnlyList<RideSample> SnapshotSamples()
    {
        lock (_gate)
        {
            return _samples.ToArray();
        }
    }

    /// <summary>Raised after every recorded sample.</summary>
    public event Action? Updated;

    /// <summary>Raised if the background pump fails (it keeps running; the message is for the diagnostics log).</summary>
    public event Action<string>? Fault;

    /// <param name="ftp">The rider's FTP at the start of the ride; stored with the ride for zone/TSS figures.</param>
    public void Start(DateTimeOffset? at = null, int? ftp = null)
    {
        lock (_gate)
        {
            if (_startedAt is not null)
            {
                return;
            }
            _startedAt = at ?? DateTimeOffset.UtcNow;
            _ftp = ftp;
            _lastSampleAt = null;
            _distance = 0;
            _powerSum = 0;
            _powerCount = 0;
            _samples.Clear();
            _sensorName = _latest?.SensorName;
            _simulated = _latest?.IsSimulated ?? false;
        }
    }

    /// <summary>Stops and returns the finished ride, or null if nothing was being recorded.</summary>
    public RideRecording? Stop(DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            if (_startedAt is not { } start)
            {
                return null;
            }
            var stop = at ?? DateTimeOffset.UtcNow;
            var samples = _samples.ToArray();
            // Compute before clearing so a failure cannot lose the ride; the caller may retry Stop().
            var summary = RideStatisticsCalculator.Compute(samples, start, stop, _ftp);
            _startedAt = null;
            return new RideRecording(start, stop, samples, summary, _sensorName, _simulated);
        }
    }

    /// <summary>Feeds one telemetry sample (also used by tests, bypassing the stream).</summary>
    public void Observe(TrainerTelemetry t)
    {
        lock (_gate)
        {
            _latest = t;
            if (_startedAt is not { } start || !t.IsDataValid || t.Timestamp < start)
            {
                return;
            }
            _sensorName ??= t.SensorName;
            _simulated |= t.IsSimulated;

            int? power = t.PowerWatts is { } p ? Math.Max(0, p) : null;
            double? cadence = t.CadenceRpm is { } c && double.IsFinite(c) ? Math.Clamp(c, 0, MaxCadenceRpm) : null;
            double? speed = t.SpeedMetersPerSecond is { } v && double.IsFinite(v) ? Math.Max(0, v) : null;

            if (speed is { } sp && _lastSampleAt is { } last)
            {
                var dt = (t.Timestamp - last).TotalSeconds;
                if (dt > 0 && dt <= MaxIntegrationGapSeconds)
                {
                    _distance += sp * dt;
                }
            }
            if (_lastSampleAt is null || t.Timestamp > _lastSampleAt)
            {
                _lastSampleAt = t.Timestamp;
            }
            if (power is { } pw)
            {
                _powerSum += pw;
                _powerCount++;
            }
            _samples.Add(new RideSample(t.Timestamp, (t.Timestamp - start).TotalSeconds, power, cadence, speed, _distance));
        }
        Updated?.Invoke();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (await _subscription.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_subscription.Reader.TryRead(out var t))
                {
                    try
                    {
                        Observe(t);
                    }
                    catch (Exception ex)
                    {
                        Fault?.Invoke($"Recorder failed to process a sample: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _subscription.Dispose();
    }
}
