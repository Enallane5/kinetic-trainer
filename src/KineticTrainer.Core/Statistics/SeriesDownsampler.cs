namespace KineticTrainer.Core.Statistics;

/// <summary>Reduces a long series to what a chart can show without losing peaks (min/max per pixel bucket).</summary>
public static class SeriesDownsampler
{
    public readonly record struct Point(double X, double Y);

    /// <summary>
    /// Splits the x-range into <paramref name="buckets"/> equal bins and emits, per non-empty bin, its minimum and
    /// maximum point in x order. Input must be sorted by x. Series no longer than <paramref name="buckets"/> are returned as-is.
    /// </summary>
    public static IReadOnlyList<Point> MinMax(IReadOnlyList<Point> points, int buckets)
    {
        if (points.Count <= Math.Max(1, buckets) * 2 || buckets < 1)
        {
            return points;
        }
        var x0 = points[0].X;
        var x1 = points[^1].X;
        if (x1 <= x0)
        {
            return [points[0], points[^1]];
        }
        var width = (x1 - x0) / buckets;
        var result = new List<Point>(buckets * 2);
        var i = 0;
        for (var b = 0; b < buckets && i < points.Count; b++)
        {
            var binEnd = b == buckets - 1 ? double.PositiveInfinity : x0 + (b + 1) * width;
            Point? min = null, max = null;
            while (i < points.Count && points[i].X < binEnd)
            {
                var p = points[i++];
                if (min is null || p.Y < min.Value.Y) min = p;
                if (max is null || p.Y > max.Value.Y) max = p;
            }
            if (min is null)
            {
                continue;
            }
            if (min.Value.X <= max!.Value.X)
            {
                result.Add(min.Value);
                if (min.Value.X != max.Value.X) result.Add(max.Value);
            }
            else
            {
                result.Add(max.Value);
                result.Add(min.Value);
            }
        }
        return result;
    }

    /// <summary>Plain rolling mean over the previous <paramref name="windowSeconds"/> of a 1 Hz series (Phase 1 3 s / 10 s averages).</summary>
    public static double? RollingAverage(IReadOnlyList<double> oneHz, int windowSeconds)
    {
        if (oneHz.Count == 0 || windowSeconds < 1)
        {
            return null;
        }
        var n = Math.Min(windowSeconds, oneHz.Count);
        double sum = 0;
        for (var i = oneHz.Count - n; i < oneHz.Count; i++)
        {
            sum += oneHz[i];
        }
        return sum / n;
    }
}
