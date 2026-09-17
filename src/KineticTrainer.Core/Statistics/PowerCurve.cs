namespace KineticTrainer.Core.Statistics;

/// <summary>
/// Best average power over fixed durations ("power curve" / mean-maximal power). Works on the 1 Hz series from
/// <see cref="RideStatisticsCalculator.Resample"/> and only uses contiguous stretches, so a best 20-minute effort can
/// never span a sensor drop-out.
/// </summary>
public static class PowerCurve
{
    /// <summary>Durations tracked for personal records, seconds.</summary>
    public static readonly IReadOnlyList<int> StandardDurations = [1, 5, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600];

    public static string Label(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        < 3600 => $"{seconds / 60} min",
        _ => $"{seconds / 3600} h",
    };

    public static IReadOnlyDictionary<int, double> Compute(IReadOnlyList<RideStatisticsCalculator.SecondSample> series, IEnumerable<int>? durations = null)
    {
        var result = new Dictionary<int, double>();
        var segments = Segments(series);
        foreach (var d in durations ?? StandardDurations)
        {
            double? best = null;
            foreach (var seg in segments)
            {
                if (BestAverage(seg, d) is { } b && (best is null || b > best))
                {
                    best = b;
                }
            }
            if (best is { } value)
            {
                result[d] = value;
            }
        }
        return result;
    }

    /// <summary>Best mean over any window of exactly <paramref name="seconds"/> consecutive values, or null if too short.</summary>
    public static double? BestAverage(IReadOnlyList<double> values, int seconds)
    {
        if (seconds < 1 || values.Count < seconds)
        {
            return null;
        }
        double sum = 0;
        for (var i = 0; i < seconds; i++)
        {
            sum += values[i];
        }
        var best = sum;
        for (var i = seconds; i < values.Count; i++)
        {
            sum += values[i] - values[i - seconds];
            if (sum > best)
            {
                best = sum;
            }
        }
        return best / seconds;
    }

    /// <summary>Splits the series into runs of consecutive seconds that all have power.</summary>
    private static List<List<double>> Segments(IReadOnlyList<RideStatisticsCalculator.SecondSample> series)
    {
        var segments = new List<List<double>>();
        List<double>? current = null;
        int? lastSecond = null;
        foreach (var s in series)
        {
            if (s.Power is not { } p)
            {
                current = null;
                lastSecond = null;
                continue;
            }
            if (current is null || lastSecond is null || s.Second != lastSecond + 1)
            {
                current = [];
                segments.Add(current);
            }
            current.Add(p);
            lastSecond = s.Second;
        }
        return segments;
    }
}
