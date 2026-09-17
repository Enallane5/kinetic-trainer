using System.Text.Json;
using System.Text.Json.Serialization;
using KineticTrainer.Workouts.Model;
using KineticTrainer.Workouts.Testing;
using KineticTrainer.Workouts.Timeline;

namespace KineticTrainer.Workouts.Playback;

public enum PlayerState
{
    Ready,
    Running,
    Paused,
    Completed,
    Stopped,
}

/// <summary>How the rider's power compares with the target (Kinetic: green on target; blue under; red over).</summary>
public enum TargetState
{
    NoTarget,
    NoData,
    Under,
    OnTarget,
    Over,
}

public enum CueKind
{
    /// <summary>A message from the workout file.</summary>
    Text,

    /// <summary>"Next: ... in 30 seconds" / "in 10 seconds".</summary>
    Upcoming,

    /// <summary>3-2-1 before a change.</summary>
    Countdown,

    /// <summary>A new work / warm-up / free-ride segment began.</summary>
    SegmentStart,

    /// <summary>A recovery segment began.</summary>
    Recovery,

    /// <summary>The cool-down began.</summary>
    Cooldown,

    /// <summary>The workout finished.</summary>
    Complete,
}

/// <summary>Something to show and optionally play/speak. <see cref="Count"/> is set for countdown ticks.</summary>
public sealed record WorkoutCue(CueKind Kind, string Message, double AtSecond, int? Count = null, int? SegmentIndex = null);

public sealed record PlayerOptions
{
    /// <summary>On-target band as a percentage of the target (the app's TargetTolerancePercent setting).</summary>
    public double TolerancePercent { get; init; } = 5;

    /// <summary>The band is never narrower than this many watts either side.</summary>
    public int MinimumBandWatts { get; init; } = 5;

    /// <summary>End a ramp test automatically after this many seconds below half the target.</summary>
    public double RampFailureSeconds { get; init; } = 10;

    public bool AutoEndRamp { get; init; } = true;

    /// <summary>Longest clock step accepted per update (guards against sleep/hibernate jumps).</summary>
    public double MaxStepSeconds { get; init; } = 10;
}

/// <summary>Inputs sampled at each update. Power drives the recording; smoothed power (3 s) drives the on-target state.</summary>
public readonly record struct PlayerInput(double? PowerWatts, double? SmoothedPowerWatts, double? CadenceRpm);

public sealed record PlayerSnapshot(
    PlayerState State,
    double ElapsedSeconds,
    int TotalSeconds,
    WorkoutSegment? Segment,
    WorkoutSegment? NextSegment,
    int? TargetWatts,
    string TargetText,
    string? NextTargetText,
    double SegmentRemainingSeconds,
    double TotalRemainingSeconds,
    TargetState TargetState,
    double? DeviationWatts,
    int? BandWatts,
    int IntensityPercent,
    bool CanEndRamp)
{
    public double Progress => TotalSeconds == 0 ? 0 : Math.Clamp(ElapsedSeconds / TotalSeconds, 0, 1);
}

/// <summary>Per-segment compliance, stored with the ride.</summary>
public sealed record SegmentCompliance(
    int Index,
    string Label,
    SegmentRole Role,
    int PlannedSeconds,
    double RiddenSeconds,
    double? TargetWatts,
    double? AverageWatts,
    double OnTargetSeconds,
    double UnderSeconds,
    double OverSeconds,
    double NoDataSeconds,
    bool Skipped);

/// <summary>What was planned vs. ridden. Serialised to JSON and stored on the ride (rides.compliance_json).</summary>
public sealed record WorkoutCompliance(
    string WorkoutName,
    int? Ftp,
    int IntensityPercent,
    int PlannedSeconds,
    double RiddenSeconds,
    bool Completed,
    double? OnTargetPercent,
    IReadOnlyList<SegmentCompliance> Segments,
    FtpTestKind FtpTest,
    int? EstimatedFtp,
    int? RampEndedAtSecond)
{
    /// <summary>Rider power (watts) for each workout second; -1 = no data. Lets history line power up with the
    /// plan exactly, even after pauses and skips. Null in older rides.</summary>
    public IReadOnlyList<int>? PowerByWorkoutSecond { get; init; }

    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static WorkoutCompliance? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<WorkoutCompliance>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Plays a structured workout against a clock. It is deterministic and UI-free: the caller calls
/// <see cref="Update"/> regularly (e.g. 4 Hz) with the current time and rider data, and receives a snapshot plus
/// cues through <see cref="CueRaised"/>. The workout clock only runs while <see cref="State"/> is Running, so a
/// pause stops targets and cues; the ride recorder is independent and keeps wall-clock time.
/// Not thread-safe: use from one thread (the UI thread).
/// </summary>
public sealed class WorkoutPlayer
{
    private const double Epsilon = 1e-6;

    private readonly PlayerOptions _options;
    private readonly double[] _powerSums;
    private readonly int[] _powerCounts;
    private readonly Accumulator[] _segments;
    private DateTimeOffset? _lastUpdate;
    private double _elapsed;
    private double _rampFailure;
    private int _intensityPercent = 100;
    private int _lastSegmentIndex = -1;
    private PlayerInput _lastInput;

    public WorkoutPlayer(Workout workout, int? ftp, PlayerOptions? options = null)
    {
        Timeline = new WorkoutTimeline(workout);
        Ftp = ftp;
        _options = options ?? new PlayerOptions();
        _powerSums = new double[Timeline.TotalSeconds];
        _powerCounts = new int[Timeline.TotalSeconds];
        _segments = Timeline.Segments.Select(_ => new Accumulator()).ToArray();
        if (workout.FtpTest == FtpTestKind.Ramp)
        {
            RampWindow = FtpEstimator.RampWindow(Timeline);
        }
    }

    public WorkoutTimeline Timeline { get; }
    public Workout Workout => Timeline.Workout;
    public int? Ftp { get; }
    public PlayerState State { get; private set; } = PlayerState.Ready;
    public double ElapsedSeconds => _elapsed;
    public int IntensityPercent => _intensityPercent;
    public int? RampEndedAtSecond { get; private set; }
    public (int Start, int End)? RampWindow { get; }

    /// <summary>True while the ramp of a ramp test is being ridden (the rider may end it).</summary>
    public bool InRamp => RampWindow is { } w && RampEndedAtSecond is null && _elapsed >= w.Start && _elapsed < w.End;

    public event Action<WorkoutCue>? CueRaised;

    public PlayerSnapshot Start(DateTimeOffset now)
    {
        if (State != PlayerState.Ready)
        {
            return Snapshot(null);
        }
        State = PlayerState.Running;
        _lastUpdate = now;
        _elapsed = 0;
        ProcessCrossings(-Epsilon, 0);
        return Snapshot(null);
    }

    public void Pause(DateTimeOffset now)
    {
        if (State != PlayerState.Running)
        {
            return;
        }
        Advance(now, _lastInput);
        State = PlayerState.Paused;
    }

    public void Resume(DateTimeOffset now)
    {
        if (State != PlayerState.Paused)
        {
            return;
        }
        State = PlayerState.Running;
        _lastUpdate = now;
    }

    public void Stop(DateTimeOffset now)
    {
        if (State is PlayerState.Running or PlayerState.Paused)
        {
            if (State == PlayerState.Running)
            {
                Advance(now, _lastInput);
            }
            State = PlayerState.Stopped;
        }
    }

    /// <summary>Sets the intensity scale (50-150 %), applied to all targets from now on.</summary>
    public void SetIntensity(int percent) => _intensityPercent = Math.Clamp(percent, 50, 150);

    /// <summary>Jumps to the start of the next segment (or finishes the workout from the last one).</summary>
    public void Skip(DateTimeOffset now)
    {
        if (State is not (PlayerState.Running or PlayerState.Paused))
        {
            return;
        }
        if (State == PlayerState.Running)
        {
            Advance(now, _lastInput);
        }
        var index = Timeline.SegmentIndexAt(_elapsed);
        if (index < 0)
        {
            return;
        }
        _segments[index].Skipped = true;
        JumpTo(Timeline.Segments[index].EndSecond, now);
    }

    /// <summary>Ramp test: the rider can no longer hold the target. Records the failure point and moves to the cool-down.</summary>
    public void EndRamp(DateTimeOffset now)
    {
        if (!InRamp || RampWindow is not { } w)
        {
            return;
        }
        if (State == PlayerState.Running)
        {
            Advance(now, _lastInput);
        }
        RampEndedAtSecond = (int)Math.Floor(_elapsed);
        for (var i = Timeline.SegmentIndexAt(_elapsed); i >= 0 && i < Timeline.Segments.Count && Timeline.Segments[i].StartSecond < w.End; i++)
        {
            _segments[i].Skipped = Timeline.Segments[i].EndSecond > _elapsed + Epsilon;
        }
        RaiseCue(new WorkoutCue(CueKind.Text, "Ramp complete. Spin easy to cool down.", _elapsed));
        JumpTo(w.End, now);
    }

    /// <summary>Advances the clock to <paramref name="now"/>, scores the rider and raises cues.</summary>
    public PlayerSnapshot Update(DateTimeOffset now, PlayerInput input)
    {
        _lastInput = input;
        if (State == PlayerState.Running)
        {
            Advance(now, input);
        }
        else
        {
            _lastUpdate = now;
        }
        return Snapshot(input);
    }

    /// <summary>The current state without advancing the clock.</summary>
    public PlayerSnapshot Snapshot(PlayerInput? input)
    {
        var seg = Timeline.SegmentAt(_elapsed);
        var next = seg is null ? null : seg.Index + 1 < Timeline.Segments.Count ? Timeline.Segments[seg.Index + 1] : null;
        var target = seg is null ? null : TargetWatts(seg, _elapsed - seg.StartSecond);
        var (state, deviation, band) = Score(seg, target, input);
        return new PlayerSnapshot(
            State,
            _elapsed,
            Timeline.TotalSeconds,
            seg,
            next,
            target,
            seg is null ? "" : Timeline.DescribeTarget(seg, Ftp, Scale),
            next is null ? null : Timeline.DescribeTarget(next, Ftp, Scale),
            seg is null ? 0 : Math.Max(0, seg.EndSecond - _elapsed),
            Math.Max(0, Timeline.TotalSeconds - _elapsed),
            state,
            deviation,
            band,
            _intensityPercent,
            InRamp && State == PlayerState.Running);
    }

    /// <summary>Average power per workout second (null where none was recorded).</summary>
    public double?[] PowerBySecond()
    {
        var result = new double?[_powerSums.Length];
        for (var i = 0; i < result.Length; i++)
        {
            if (_powerCounts[i] > 0)
            {
                result[i] = _powerSums[i] / _powerCounts[i];
            }
        }
        // Bridge single missing seconds (a 2 Hz sensor can miss a second boundary), never longer gaps.
        for (var i = 1; i < result.Length - 1; i++)
        {
            if (result[i] is null && result[i - 1] is { } prev && result[i + 1] is not null && _powerCounts[i] == 0)
            {
                result[i] = prev;
            }
        }
        return result;
    }

    public FtpEstimate? EstimateFtp() =>
        Workout.FtpTest == FtpTestKind.None ? null : FtpEstimator.Estimate(Timeline, PowerBySecond(), Ftp, RampEndedAtSecond);

    public WorkoutCompliance BuildCompliance(FtpEstimate? estimate = null)
    {
        double on = 0, scored = 0;
        var segments = new List<SegmentCompliance>();
        for (var i = 0; i < Timeline.Segments.Count; i++)
        {
            var seg = Timeline.Segments[i];
            var a = _segments[i];
            if (a.Ridden <= 0 && !a.Skipped)
            {
                continue;
            }
            if (seg.HasTarget)
            {
                on += a.On;
                scored += a.On + a.Under + a.Over;
            }
            segments.Add(new SegmentCompliance(
                seg.Index,
                seg.Label,
                seg.Role,
                seg.DurationSeconds,
                Math.Round(a.Ridden, 1),
                a.TargetTime > 0 ? Math.Round(a.TargetSum / a.TargetTime, 1) : null,
                a.PowerTime > 0 ? Math.Round(a.PowerSum / a.PowerTime, 1) : null,
                Math.Round(a.On, 1),
                Math.Round(a.Under, 1),
                Math.Round(a.Over, 1),
                Math.Round(a.NoData, 1),
                a.Skipped));
        }
        return new WorkoutCompliance(
            Workout.Name,
            Ftp,
            _intensityPercent,
            Timeline.TotalSeconds,
            Math.Round(_segments.Sum(s => s.Ridden), 1),
            State == PlayerState.Completed,
            scored > 0 ? Math.Round(on / scored * 100, 1) : null,
            segments,
            Workout.FtpTest,
            estimate?.Ftp,
            RampEndedAtSecond)
        {
            PowerByWorkoutSecond = PowerBySecond().Select(p => p is { } v ? (int)Math.Round(v) : -1).ToArray(),
        };
    }

    private double Scale => _intensityPercent / 100.0;

    private int? TargetWatts(WorkoutSegment seg, double offset) =>
        Timeline.ToWatts(seg.PowerAt(offset), Ftp, Scale);

    private (TargetState State, double? Deviation, int? Band) Score(WorkoutSegment? seg, int? target, PlayerInput? input)
    {
        if (seg is null || target is not { } t || seg.Role == SegmentRole.MaxEffort)
        {
            return (TargetState.NoTarget, null, null);
        }
        var band = (int)Math.Max(_options.MinimumBandWatts, Math.Round(t * _options.TolerancePercent / 100.0));
        var p = input?.SmoothedPowerWatts ?? input?.PowerWatts;
        if (p is not { } power || !double.IsFinite(power))
        {
            return (TargetState.NoData, null, band);
        }
        var d = power - t;
        var state = d < -band ? TargetState.Under : d > band ? TargetState.Over : TargetState.OnTarget;
        return (state, d, band);
    }

    private void Advance(DateTimeOffset now, PlayerInput input)
    {
        var last = _lastUpdate ?? now;
        _lastUpdate = now;
        var dt = Math.Clamp((now - last).TotalSeconds, 0, _options.MaxStepSeconds);
        if (dt <= 0 || State != PlayerState.Running)
        {
            return;
        }
        var from = _elapsed;
        var to = Math.Min(Timeline.TotalSeconds, from + dt);

        // Score the stretch just ridden against the segment(s) it covered.
        var cursor = from;
        while (cursor < to - Epsilon)
        {
            var index = Timeline.SegmentIndexAt(cursor);
            if (index < 0)
            {
                break;
            }
            var seg = Timeline.Segments[index];
            var a = _segments[index];
            var spanEnd = Math.Min(to, seg.EndSecond);
            var span = spanEnd - cursor;
            a.Ridden += span;
            var target = TargetWatts(seg, cursor - seg.StartSecond);
            var (state, _, _) = Score(seg, target, input);
            switch (state)
            {
                case TargetState.OnTarget: a.On += span; break;
                case TargetState.Under: a.Under += span; break;
                case TargetState.Over: a.Over += span; break;
                case TargetState.NoData: a.NoData += span; break;
            }
            if (target is { } tw)
            {
                a.TargetSum += tw * span;
                a.TargetTime += span;
            }
            if (input.PowerWatts is { } pw && double.IsFinite(pw))
            {
                a.PowerSum += Math.Max(0, pw) * span;
                a.PowerTime += span;
            }

            // Ramp failure: well below target for a sustained stretch ends the ramp. A sensor drop-out (no data)
            // neither counts towards failure nor resets it.
            if (_options.AutoEndRamp && RampWindow is { } rw && RampEndedAtSecond is null
                && cursor >= rw.Start + 60 && cursor < rw.End && target is { } rt)
            {
                var p = input.SmoothedPowerWatts ?? input.PowerWatts;
                if (p is { } pv)
                {
                    _rampFailure = pv < rt * 0.5 ? _rampFailure + span : 0;
                }
            }
            cursor = spanEnd;
        }
        if (input.PowerWatts is { } power && double.IsFinite(power))
        {
            // The reading covers (from, to]; file it under the second containing the middle of that stretch.
            var second = (int)Math.Floor((from + to) / 2);
            if (second >= 0 && second < _powerSums.Length)
            {
                _powerSums[second] += Math.Max(0, power);
                _powerCounts[second]++;
            }
        }

        _elapsed = to;
        ProcessCrossings(from, to);

        if (_rampFailure >= _options.RampFailureSeconds && InRamp)
        {
            _rampFailure = 0;
            EndRamp(now);
        }
    }

    private void JumpTo(double second, DateTimeOffset now)
    {
        _elapsed = Math.Min(second, Timeline.TotalSeconds);
        _lastUpdate = now;
        _lastSegmentIndex = -1;
        ProcessCrossings(_elapsed - Epsilon, _elapsed);
    }

    /// <summary>Raises every cue scheduled in (from, to]. If several segment starts were crossed, only the last is announced.</summary>
    private void ProcessCrossings(double from, double to)
    {
        if (to <= from)
        {
            return;
        }
        var segments = Timeline.Segments;

        foreach (var te in Timeline.TextEvents)
        {
            if (te.AtSecond > from && te.AtSecond <= to)
            {
                RaiseCue(new WorkoutCue(CueKind.Text, te.Message, te.AtSecond));
            }
        }

        // Warnings and countdowns before each change (not before the very first segment).
        for (var i = 1; i < segments.Count; i++)
        {
            var prev = segments[i - 1];
            var next = segments[i];
            var b = next.StartSecond;
            if (b - 30 > to || b - 1 <= from)
            {
                continue;
            }
            if (!IsNotableChange(prev, next))
            {
                continue;
            }
            if (prev.DurationSeconds >= 90 && Crossed(b - 30, from, to))
            {
                RaiseCue(new WorkoutCue(CueKind.Upcoming, $"Next: {Describe(next)} in 30 seconds", b - 30, SegmentIndex: next.Index));
            }
            if (prev.DurationSeconds >= 20 && Crossed(b - 10, from, to))
            {
                RaiseCue(new WorkoutCue(CueKind.Upcoming, $"{next.Label} in 10 seconds", b - 10, SegmentIndex: next.Index));
            }
            if (prev.DurationSeconds >= 6)
            {
                for (var c = 3; c >= 1; c--)
                {
                    if (Crossed(b - c, from, to))
                    {
                        RaiseCue(new WorkoutCue(CueKind.Countdown, c.ToString(), b - c, Count: c, SegmentIndex: next.Index));
                    }
                }
            }
        }
        // Countdown to the end of the workout.
        var end = Timeline.TotalSeconds;
        if (segments.Count > 0 && segments[^1].DurationSeconds >= 6)
        {
            for (var c = 3; c >= 1; c--)
            {
                if (Crossed(end - c, from, to))
                {
                    RaiseCue(new WorkoutCue(CueKind.Countdown, c.ToString(), end - c, Count: c));
                }
            }
        }

        // Segment start announcement.
        var current = Timeline.SegmentIndexAt(to);
        if (current >= 0 && current != _lastSegmentIndex)
        {
            var started = segments[current];
            if (started.StartSecond > from || _lastSegmentIndex == -1)
            {
                var kind = started.Role switch
                {
                    SegmentRole.Recovery => CueKind.Recovery,
                    SegmentRole.Cooldown => CueKind.Cooldown,
                    _ => CueKind.SegmentStart,
                };
                RaiseCue(new WorkoutCue(kind, StartMessage(started), started.StartSecond, SegmentIndex: started.Index));
            }
            _lastSegmentIndex = current;
        }

        if (to >= end && State is PlayerState.Running or PlayerState.Paused)
        {
            State = PlayerState.Completed;
            RaiseCue(new WorkoutCue(CueKind.Complete, "Workout complete. Great job!", end));
        }
    }

    private static bool Crossed(double at, double from, double to) => at >= 0 && at > from && at <= to;

    private bool IsNotableChange(WorkoutSegment prev, WorkoutSegment next)
    {
        if (prev.Role != next.Role || prev.HasTarget != next.HasTarget)
        {
            return true;
        }
        var a = Timeline.ToFraction(prev.PowerEnd, Ftp);
        var b = Timeline.ToFraction(next.PowerStart, Ftp);
        if (a is { } x && b is { } y)
        {
            return Math.Abs(x - y) >= 0.05;
        }
        return prev.PowerEnd != next.PowerStart;
    }

    private string Describe(WorkoutSegment seg) =>
        $"{seg.Label}, {Timeline.DescribeTarget(seg, Ftp, Scale)} for {FormatDuration(seg.DurationSeconds)}";

    private string StartMessage(WorkoutSegment seg)
    {
        var target = Timeline.DescribeTarget(seg, Ftp, Scale);
        var dur = FormatDuration(seg.DurationSeconds);
        return seg.Role switch
        {
            SegmentRole.Warmup => $"Warm-up: {target} for {dur}",
            SegmentRole.Recovery => $"Recover: {target} for {dur}",
            SegmentRole.Cooldown => $"Cool-down: {target} for {dur}",
            SegmentRole.FreeRide => $"Free ride for {dur}",
            SegmentRole.MaxEffort => $"All out for {dur}!",
            _ => $"{seg.Label}: {target} for {dur}",
        };
    }

    public static string FormatDuration(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds} seconds";
        }
        var m = seconds / 60;
        var s = seconds % 60;
        var minutes = m == 1 ? "1 minute" : $"{m} minutes";
        return s == 0 ? minutes : $"{minutes} {s} seconds";
    }

    private void RaiseCue(WorkoutCue cue) => CueRaised?.Invoke(cue);

    private sealed class Accumulator
    {
        public double Ridden;
        public double On;
        public double Under;
        public double Over;
        public double NoData;
        public double TargetSum;
        public double TargetTime;
        public double PowerSum;
        public double PowerTime;
        public bool Skipped;
    }
}
