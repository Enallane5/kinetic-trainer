using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Zones;
using KineticTrainer.Workouts.Model;

namespace KineticTrainer.Workouts.Timeline;

/// <summary>What a segment is for; drives labels, cue wording and compliance scoring.</summary>
public enum SegmentRole
{
    Warmup,
    Work,
    Recovery,
    Cooldown,
    FreeRide,
    MaxEffort,
}

/// <summary>
/// One contiguous stretch with a single (possibly ramped) target: a non-interval step, or one on/off phase of an
/// interval block. Times are whole seconds from the start of the workout.
/// </summary>
public sealed record WorkoutSegment
{
    public required int Index { get; init; }
    public required int StepIndex { get; init; }
    public required WorkoutStepKind Kind { get; init; }
    public required SegmentRole Role { get; init; }
    public required int StartSecond { get; init; }
    public required int DurationSeconds { get; init; }

    /// <summary>Target at the start and end of the segment, in the workout's power unit; null = no target.</summary>
    public double? PowerStart { get; init; }
    public double? PowerEnd { get; init; }

    public int? Cadence { get; init; }

    /// <summary>1-based repeat number and count for interval phases.</summary>
    public int? Repeat { get; init; }
    public int? RepeatCount { get; init; }

    /// <summary>True for the "on" phase of an interval block.</summary>
    public bool IsOnPhase { get; init; }

    public required string Label { get; init; }

    public int EndSecond => StartSecond + DurationSeconds;
    public bool HasTarget => PowerStart is not null;
    public bool IsRamp => PowerStart is { } a && PowerEnd is { } b && Math.Abs(a - b) > 1e-9;

    /// <summary>Mean target over the segment.</summary>
    public double? AveragePower => PowerStart is { } a && PowerEnd is { } b ? (a + b) / 2 : null;

    /// <summary>Target at an offset (seconds, may be fractional) into the segment, linearly interpolated.</summary>
    public double? PowerAt(double offsetSeconds)
    {
        if (PowerStart is not { } a || PowerEnd is not { } b)
        {
            return null;
        }
        if (DurationSeconds <= 0)
        {
            return a;
        }
        var f = Math.Clamp(offsetSeconds / DurationSeconds, 0, 1);
        return a + ((b - a) * f);
    }
}

/// <summary>A text message at an absolute time.</summary>
public sealed record TimelineTextEvent(int AtSecond, string Message);

/// <summary>
/// A workout flattened onto a time line. All workout logic (player, charts, metrics, FTP test windows) works
/// from this, so a step type only has to be understood in one place.
/// </summary>
public sealed class WorkoutTimeline
{
    /// <summary>Steady steps at or below this fraction of FTP (outside warm-up/cool-down) count as recovery.</summary>
    public const double RecoveryThreshold = 0.60;

    /// <summary>Targets at or above this fraction of FTP are shown as "MAX" rather than watts.</summary>
    public const double MaxDisplayThreshold = 2.0;

    private readonly int[] _starts;

    public WorkoutTimeline(Workout workout)
    {
        Workout = workout ?? throw new ArgumentNullException(nameof(workout));
        var segments = new List<WorkoutSegment>();
        var events = new List<TimelineTextEvent>();
        var t = 0;
        var workIndex = 0;
        var totalWork = CountWorkBlocks(workout);
        for (var si = 0; si < workout.Steps.Count; si++)
        {
            var s = workout.Steps[si];
            foreach (var te in s.TextEvents)
            {
                if (te.OffsetSeconds < s.TotalSeconds)
                {
                    events.Add(new TimelineTextEvent(t + te.OffsetSeconds, te.Message));
                }
            }
            switch (s.Kind)
            {
                case WorkoutStepKind.Intervals:
                {
                    var repeat = Math.Max(1, s.Repeat);
                    for (var r = 1; r <= repeat; r++)
                    {
                        if (s.OnDurationSeconds > 0)
                        {
                            var onRole = RoleForFlat(s.OnPowerLow, s.OnPowerHigh);
                            segments.Add(new WorkoutSegment
                            {
                                Index = segments.Count, StepIndex = si, Kind = s.Kind, Role = onRole, StartSecond = t,
                                DurationSeconds = s.OnDurationSeconds, PowerStart = s.OnPowerLow, PowerEnd = s.OnPowerHigh,
                                Cadence = s.Cadence, Repeat = r, RepeatCount = repeat, IsOnPhase = true,
                                Label = repeat > 1 ? $"Interval {r} of {repeat}" : "Interval",
                            });
                            t += s.OnDurationSeconds;
                        }
                        if (s.OffDurationSeconds > 0)
                        {
                            var offRole = RoleForFlat(s.OffPowerLow, s.OffPowerHigh);
                            segments.Add(new WorkoutSegment
                            {
                                Index = segments.Count, StepIndex = si, Kind = s.Kind, Role = offRole, StartSecond = t,
                                DurationSeconds = s.OffDurationSeconds, PowerStart = s.OffPowerLow, PowerEnd = s.OffPowerHigh,
                                Cadence = s.CadenceResting, Repeat = r, RepeatCount = repeat, IsOnPhase = false,
                                Label = offRole == SegmentRole.Recovery
                                    ? (repeat > 1 ? $"Recovery {r} of {repeat}" : "Recovery")
                                    : (repeat > 1 ? $"Interval {r} of {repeat} (part 2)" : "Interval (part 2)"),
                            });
                            t += s.OffDurationSeconds;
                        }
                    }
                    break;
                }
                default:
                {
                    var role = s.Kind switch
                    {
                        WorkoutStepKind.Warmup => SegmentRole.Warmup,
                        WorkoutStepKind.Cooldown => SegmentRole.Cooldown,
                        WorkoutStepKind.FreeRide => SegmentRole.FreeRide,
                        WorkoutStepKind.MaxEffort => SegmentRole.MaxEffort,
                        _ => RoleForFlat(s.PowerLow, s.PowerHigh),
                    };
                    if (role is SegmentRole.Work or SegmentRole.MaxEffort)
                    {
                        workIndex++;
                    }
                    var label = role switch
                    {
                        SegmentRole.Warmup => "Warm-up",
                        SegmentRole.Cooldown => "Cool-down",
                        SegmentRole.FreeRide => "Free ride",
                        SegmentRole.MaxEffort => "Max effort",
                        SegmentRole.Recovery => "Recovery",
                        _ => s.Kind == WorkoutStepKind.Ramp ? "Ramp" : (totalWork > 1 ? $"Effort {workIndex} of {totalWork}" : "Effort"),
                    };
                    segments.Add(new WorkoutSegment
                    {
                        Index = segments.Count, StepIndex = si, Kind = s.Kind, Role = role, StartSecond = t,
                        DurationSeconds = s.DurationSeconds,
                        PowerStart = s.HasPowerTarget ? s.PowerLow : null,
                        PowerEnd = s.HasPowerTarget ? s.PowerHigh : null,
                        Cadence = s.Cadence ?? AverageCadence(s),
                        Label = label,
                    });
                    t += s.DurationSeconds;
                    break;
                }
            }
        }
        foreach (var te in workout.WorkoutTextEvents)
        {
            if (te.OffsetSeconds < t)
            {
                events.Add(new TimelineTextEvent(te.OffsetSeconds, te.Message));
            }
        }
        Relabel(workout, segments);
        Segments = segments;
        TextEvents = events.OrderBy(e => e.AtSecond).ToArray();
        TotalSeconds = t;
        _starts = segments.Select(s => s.StartSecond).ToArray();
    }

    public Workout Workout { get; }
    public IReadOnlyList<WorkoutSegment> Segments { get; }
    public IReadOnlyList<TimelineTextEvent> TextEvents { get; }
    public int TotalSeconds { get; }

    /// <summary>Index of the segment containing <paramref name="second"/>, or -1 before the start / at or after the end.</summary>
    public int SegmentIndexAt(double second)
    {
        if (second < 0 || second >= TotalSeconds || _starts.Length == 0)
        {
            return -1;
        }
        var i = Array.BinarySearch(_starts, (int)Math.Floor(second));
        if (i < 0)
        {
            i = ~i - 1;
        }
        // Zero-length segments cannot exist (durations are validated), but skip forward defensively.
        while (i < Segments.Count - 1 && Segments[i].EndSecond <= second)
        {
            i++;
        }
        return i;
    }

    public WorkoutSegment? SegmentAt(double second) => SegmentIndexAt(second) is var i and >= 0 ? Segments[i] : null;

    /// <summary>Target in the workout's unit at a time, or null (free ride / max effort / outside the workout).</summary>
    public double? TargetAt(double second) =>
        SegmentAt(second) is { } seg ? seg.PowerAt(second - seg.StartSecond) : null;

    /// <summary>Target in watts at a time for a rider, including an intensity scale (1.0 = as written).</summary>
    public int? TargetWattsAt(double second, int? ftp, double intensity = 1.0) =>
        ToWatts(TargetAt(second), ftp, intensity);

    public int? ToWatts(double? target, int? ftp, double intensity = 1.0)
    {
        if (target is not { } v)
        {
            return null;
        }
        if (Workout.PowerUnit == WorkoutPowerUnit.Watts)
        {
            return (int)Math.Round(v * intensity, MidpointRounding.AwayFromZero);
        }
        if (ftp is not { } f || f <= 0)
        {
            return null;
        }
        return (int)Math.Round(v * f * intensity, MidpointRounding.AwayFromZero);
    }

    /// <summary>Target as a fraction of FTP (for zone colours), or null when that cannot be known.</summary>
    public double? ToFraction(double? target, int? ftp)
    {
        if (target is not { } v)
        {
            return null;
        }
        if (Workout.PowerUnit == WorkoutPowerUnit.FtpFraction)
        {
            return v;
        }
        return ftp is { } f && f > 0 ? v / f : null;
    }

    /// <summary>Human text for a target: "240 W", "MAX", or "Free ride".</summary>
    public string DescribeTarget(WorkoutSegment segment, int? ftp, double intensity = 1.0)
    {
        if (segment.Role == SegmentRole.MaxEffort)
        {
            return "MAX";
        }
        if (!segment.HasTarget)
        {
            return "Free ride";
        }
        var fraction = ToFraction(segment.PowerStart, ftp);
        if (fraction is >= MaxDisplayThreshold)
        {
            return "MAX";
        }
        var a = ToWatts(segment.PowerStart, ftp, intensity);
        var b = ToWatts(segment.PowerEnd, ftp, intensity);
        if (a is null || b is null)
        {
            return segment.IsRamp
                ? $"{segment.PowerStart * 100:0}–{segment.PowerEnd * 100:0}% FTP"
                : $"{segment.PowerStart * 100:0}% FTP";
        }
        return a == b ? $"{a} W" : $"{a}–{b} W";
    }

    /// <summary>"125–175 W (50–70%)": watts for the rider plus the percentage of FTP.</summary>
    public string DescribeTargetWithPercent(WorkoutSegment segment, int? ftp, double intensity = 1.0)
    {
        var text = DescribeTarget(segment, ftp, intensity);
        if (!segment.HasTarget || text == "MAX" || text.Contains('%'))
        {
            return text;
        }
        var a = ToFraction(segment.PowerStart, ftp) * intensity * 100;
        var b = ToFraction(segment.PowerEnd, ftp) * intensity * 100;
        if (a is null || b is null)
        {
            return text;
        }
        var pct = Math.Abs(a.Value - b.Value) < 0.5 ? $"{a:0}%" : $"{a:0}–{b:0}%";
        return $"{text} ({pct})";
    }

    /// <summary>1 Hz series of target fractions of FTP (null where there is no target). Used for charts and metrics.</summary>
    public double?[] FractionSeries(int? ftp = null)
    {
        var series = new double?[TotalSeconds];
        foreach (var seg in Segments)
        {
            for (var i = 0; i < seg.DurationSeconds; i++)
            {
                series[seg.StartSecond + i] = ToFraction(seg.PowerAt(i + 0.5), ftp);
            }
        }
        return series;
    }

    /// <summary>Steady work below this fraction of FTP is labelled "Endurance" rather than numbered as an effort.</summary>
    public const double EffortThreshold = 0.75;

    /// <summary>
    /// Second pass over the segments: easy steps before the first effort are the warm-up and easy steps after the
    /// last effort are the cool-down; ramp-test steps are numbered; steady endurance work is named, not numbered.
    /// </summary>
    private static void Relabel(Workout workout, List<WorkoutSegment> segs)
    {
        if (segs.Count == 0)
        {
            return;
        }
        var firstWork = segs.FindIndex(s => s.Role is SegmentRole.Work or SegmentRole.MaxEffort);
        var lastWork = segs.FindLastIndex(s => s.Role is SegmentRole.Work or SegmentRole.MaxEffort);
        for (var i = 0; i < segs.Count; i++)
        {
            if (segs[i].Role != SegmentRole.Recovery || firstWork < 0)
            {
                continue;
            }
            if (i < firstWork)
            {
                segs[i] = segs[i] with { Role = SegmentRole.Warmup, Label = "Warm-up" };
            }
            else if (i > lastWork)
            {
                segs[i] = segs[i] with { Role = SegmentRole.Cooldown, Label = "Cool-down" };
            }
        }

        if (workout.FtpTest == FtpTestKind.Ramp && segs.Count >= 3)
        {
            var cooldown = segs.FindLastIndex(s => s.Role == SegmentRole.Cooldown);
            var end = cooldown > 0 ? cooldown : segs.Count;
            var n = end - 1;
            for (var i = 1; i < end; i++)
            {
                segs[i] = segs[i] with { Role = SegmentRole.Work, Label = $"Ramp step {i} of {n}" };
            }
            segs[0] = segs[0] with { Role = SegmentRole.Warmup, Label = "Warm-up" };
            return;
        }

        var efforts = segs.Where(IsNumberedEffort).ToList();
        var k = 0;
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Kind != WorkoutStepKind.SteadyState || s.Role != SegmentRole.Work)
            {
                continue;
            }
            if (!IsNumberedEffort(s))
            {
                segs[i] = s with { Label = "Endurance" };
            }
            else
            {
                k++;
                segs[i] = s with { Label = efforts.Count > 1 ? $"Effort {k} of {efforts.Count}" : "Effort" };
            }
        }
    }

    private static bool IsNumberedEffort(WorkoutSegment s) =>
        s.Kind == WorkoutStepKind.SteadyState && s.Role == SegmentRole.Work && Math.Max(s.PowerStart ?? 0, s.PowerEnd ?? 0) > EffortThreshold;

    private static SegmentRole RoleForFlat(double low, double high) =>
        Math.Max(low, high) <= RecoveryThreshold ? SegmentRole.Recovery : SegmentRole.Work;

    private static int CountWorkBlocks(Workout w) => w.Steps.Count(s =>
        s.Kind is WorkoutStepKind.SteadyState or WorkoutStepKind.Ramp && Math.Max(s.PowerLow, s.PowerHigh) > RecoveryThreshold
        || s.Kind == WorkoutStepKind.MaxEffort);

    private static int? AverageCadence(WorkoutStep s) =>
        s.CadenceLow is { } lo && s.CadenceHigh is { } hi ? (lo + hi) / 2 : s.CadenceLow ?? s.CadenceHigh;
}

/// <summary>Planned-workout figures shown in the library (Kinetic Fit's list shows duration, IF and TSS).</summary>
public sealed record WorkoutMetrics(
    int TotalSeconds,
    double? IntensityFactor,
    double? TrainingStressScore,
    double? AverageFraction,
    int[] ZoneSeconds,
    double? WorkKilojoules)
{
    /// <summary>
    /// Computes planned IF/TSS from the target series with the same Coggan NP method used for rides. Free-ride and
    /// max-effort seconds have no target and are left out. Only meaningful for FTP-relative workouts (or when an FTP
    /// is supplied for watts workouts).
    /// </summary>
    public static WorkoutMetrics For(WorkoutTimeline timeline, int? ftp = null)
    {
        var series = timeline.FractionSeries(ftp);
        var values = series.Where(v => v is not null).Select(v => Math.Min(v!.Value, WorkoutTimeline.MaxDisplayThreshold)).ToArray();
        var zones = new int[PowerZones.Coggan.Count];
        foreach (var v in values)
        {
            zones[PowerZones.ZoneForFraction(v).Number - 1]++;
        }
        if (values.Length == 0)
        {
            return new WorkoutMetrics(timeline.TotalSeconds, null, null, null, zones, null);
        }
        // NP is scale-free, so it can be computed directly on FTP fractions.
        var intensity = NormalizedPowerCalculator.Compute(values) ?? values.Average();
        var tss = values.Length / 3600.0 * intensity * intensity * 100;
        double? kj = ftp is { } f && f > 0 ? values.Sum(v => v * f) / 1000.0 : null;
        return new WorkoutMetrics(timeline.TotalSeconds, intensity, tss, values.Average(), zones, kj);
    }
}
