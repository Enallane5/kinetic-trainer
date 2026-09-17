using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Zones;
using KineticTrainer.Workouts.Model;
using KineticTrainer.Workouts.Timeline;

namespace KineticTrainer.Workouts.Testing;

/// <summary>The outcome of an FTP test. <see cref="Ftp"/> is null when no trustworthy estimate could be made.</summary>
public sealed record FtpEstimate(
    FtpTestKind Test,
    int? Ftp,
    double? BasisWatts,
    double Factor,
    string Method,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Ftp is not null;
}

/// <summary>
/// Estimates FTP from a ridden test. The formulas are the published ones:
/// <list type="bullet">
/// <item>20-minute (Allen/Coggan): 95 % of the average power of the 20-minute effort.</item>
/// <item>Ramp (Zwift/TrainerRoad): 75 % of the best 60-second power inside the ramp.</item>
/// <item>8-minute (CTS): 90 % of the better of the two 8-minute efforts.</item>
/// </list>
/// Safeguards: the effort windows come from the workout timeline (not guessed from the ride), the rider must have
/// ridden the whole effort with power data for most of it, and the result must be a plausible FTP.
/// Works on workout-clock time (the player's per-second power), so pauses and skips do not shift the windows.
/// </summary>
public static class FtpEstimator
{
    public const double TwentyMinuteFactor = 0.95;
    public const double RampFactor = 0.75;
    public const double EightMinuteFactor = 0.90;

    /// <summary>At least this share of an effort window must have power data.</summary>
    public const double MinimumCoverage = 0.90;

    /// <summary>Changes larger than this (relative to the current FTP) are flagged for the rider to double-check.</summary>
    public const double LargeChangeFraction = 0.15;

    /// <summary>
    /// Power sampled on the workout clock: <paramref name="powerAtSecond"/>[s] is the rider's 1 Hz power at workout
    /// second s (null = no data or not ridden).
    /// </summary>
    public static FtpEstimate Estimate(WorkoutTimeline timeline, IReadOnlyList<double?> powerAtSecond, int? currentFtp, int? rampEndedAtSecond = null)
    {
        var kind = timeline.Workout.FtpTest;
        var warnings = new List<string>();
        FtpEstimate Invalid(string method, string reason, double factor) =>
            new(kind, null, null, factor, method, [.. warnings, reason]);

        switch (kind)
        {
            case FtpTestKind.TwentyMinute:
            {
                const string method = "95% of your 20-minute average power";
                var effort = timeline.Segments.LastOrDefault(s => s.DurationSeconds == 1200 && s.HasTarget && s.Role == SegmentRole.Work);
                if (effort is null)
                {
                    return Invalid(method, "This workout has no 20-minute effort.", TwentyMinuteFactor);
                }
                if (WindowAverage(powerAtSecond, effort.StartSecond, effort.DurationSeconds, warnings, "20-minute effort") is not { } avg)
                {
                    return Invalid(method, "The 20-minute effort was not completed with power data.", TwentyMinuteFactor);
                }
                return Finish(kind, avg, TwentyMinuteFactor, method, currentFtp, warnings);
            }
            case FtpTestKind.EightMinute:
            {
                const string method = "90% of your better 8-minute average power";
                var efforts = timeline.Segments.Where(s => s.DurationSeconds == 480 && s.HasTarget && s.Role == SegmentRole.Work).ToList();
                if (efforts.Count == 0)
                {
                    return Invalid(method, "This workout has no 8-minute efforts.", EightMinuteFactor);
                }
                double? best = null;
                var n = 0;
                foreach (var e in efforts)
                {
                    n++;
                    if (WindowAverage(powerAtSecond, e.StartSecond, e.DurationSeconds, warnings, $"8-minute effort {n}") is { } avg && (best is null || avg > best))
                    {
                        best = avg;
                    }
                }
                if (best is not { } b)
                {
                    return Invalid(method, "Neither 8-minute effort was completed with power data.", EightMinuteFactor);
                }
                return Finish(kind, b, EightMinuteFactor, method, currentFtp, warnings);
            }
            case FtpTestKind.Ramp:
            {
                const string method = "75% of your best 1-minute power in the ramp";
                var (start, end) = RampWindow(timeline);
                if (end - start < 60)
                {
                    return Invalid(method, "This workout has no ramp section.", RampFactor);
                }
                if (rampEndedAtSecond is { } ended)
                {
                    end = Math.Clamp(ended, start, end);
                }
                // Best 60 s over contiguous seconds with power (never across a drop-out).
                var runs = new List<List<double>>();
                List<double>? run = null;
                for (var s = start; s < end && s < powerAtSecond.Count; s++)
                {
                    if (powerAtSecond[s] is { } p && double.IsFinite(p))
                    {
                        run ??= [];
                        if (run.Count == 0)
                        {
                            runs.Add(run);
                        }
                        run.Add(Math.Max(0, p));
                    }
                    else
                    {
                        run = null;
                    }
                }
                double? best = null;
                foreach (var r in runs)
                {
                    if (PowerCurve.BestAverage(r, 60) is { } v && (best is null || v > best))
                    {
                        best = v;
                    }
                }
                if (best is not { } b)
                {
                    return Invalid(method, "Less than one continuous minute of the ramp was ridden with power data.", RampFactor);
                }
                var rampSeconds = end - start;
                if (rampSeconds < 4 * 60)
                {
                    warnings.Add("The ramp ended within 4 minutes; if you stopped early for another reason, the estimate will be too low.");
                }
                return Finish(kind, b, RampFactor, method, currentFtp, warnings);
            }
            default:
                return new FtpEstimate(kind, null, null, 1, "", ["This workout is not an FTP test."]);
        }
    }

    /// <summary>The ramp part of a ramp test: from the end of the first step (warm-up) to the start of the cool-down.</summary>
    public static (int Start, int End) RampWindow(WorkoutTimeline timeline)
    {
        var segs = timeline.Segments;
        if (segs.Count < 3)
        {
            return (0, 0);
        }
        var start = segs[0].EndSecond;
        var cooldown = segs.LastOrDefault(s => s.Role == SegmentRole.Cooldown);
        var end = cooldown?.StartSecond ?? timeline.TotalSeconds;
        return (start, Math.Max(start, end));
    }

    private static double? WindowAverage(IReadOnlyList<double?> power, int start, int length, List<string> warnings, string what)
    {
        if (start + length > power.Count)
        {
            return null;
        }
        double sum = 0;
        var n = 0;
        for (var s = start; s < start + length; s++)
        {
            if (power[s] is { } p && double.IsFinite(p))
            {
                sum += Math.Max(0, p);
                n++;
            }
        }
        if (n < length * MinimumCoverage)
        {
            return null;
        }
        if (n < length)
        {
            warnings.Add($"The {what} had {length - n} s without power data; the average uses the rest.");
        }
        return sum / n;
    }

    private static FtpEstimate Finish(FtpTestKind kind, double basis, double factor, string method, int? currentFtp, List<string> warnings)
    {
        var ftp = (int)Math.Round(basis * factor, MidpointRounding.AwayFromZero);
        if (!PowerZones.IsValidFtp(ftp))
        {
            return new FtpEstimate(kind, null, basis, factor, method,
                [.. warnings, $"The result ({ftp} W) is outside the plausible range {PowerZones.MinimumFtp}–{PowerZones.MaximumFtp} W."]);
        }
        if (currentFtp is { } cur && cur > 0 && Math.Abs(ftp - cur) > cur * LargeChangeFraction)
        {
            warnings.Add($"This is a {(ftp > cur ? "rise" : "drop")} of {Math.Abs(ftp - cur) * 100.0 / cur:0}% from your current FTP of {cur} W. Check the test went as planned before saving it.");
        }
        return new FtpEstimate(kind, ftp, basis, factor, method, warnings);
    }
}
