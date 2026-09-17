using KineticTrainer.Core.Rides;
using KineticTrainer.Core.Zones;

namespace KineticTrainer.Core.Statistics;

/// <summary>
/// Computes ride statistics from recorded samples. Time-based quantities (averages, Normalized Power, work) are
/// computed on a 1 Hz resampled series so a sensor sending at 2 Hz and one sending at 1 Hz produce the same numbers;
/// maxima come from the raw samples.
/// </summary>
public static class RideStatisticsCalculator
{
    /// <summary>A gap in sensor data longer than this many missing seconds is not bridged by carrying the last value forward.</summary>
    public const int MaxCarryForwardSeconds = 3;

    /// <summary>Distance is integrated across two consecutive samples at most this far apart (the same "dropped packet" rule).</summary>
    public const double MaxIntegrationGapSeconds = MaxCarryForwardSeconds + 1;

    public static RideSummary Compute(IReadOnlyList<RideSample> samples, DateTimeOffset start, DateTimeOffset end, int? ftp = null)
    {
        var elapsed = Math.Max(0, (end - start).TotalSeconds);
        var validFtp = PowerZones.IsValidFtp(ftp) ? ftp : null;
        if (samples.Count == 0)
        {
            return new RideSummary
            {
                StartTime = start, EndTime = end, ElapsedSeconds = elapsed, MovingSeconds = 0, DistanceMeters = 0, SampleCount = 0, Ftp = validFtp,
            };
        }

        var series = Resample(samples, elapsed);
        var powers = series.Where(s => s.Power is not null).Select(s => s.Power!.Value).ToList();
        var cadences = series.Where(s => s.Cadence is not null).Select(s => s.Cadence!.Value).ToList();
        var speeds = series.Where(s => s.Speed is not null).Select(s => s.Speed!.Value).ToList();
        var moving = series.Count(s => s.Speed is > 0);
        var np = NormalizedPowerCalculator.Compute(powers);
        double? intensity = validFtp is { } f && np is { } n ? n / f : null;
        double? tss = validFtp is { } f2 && np is { } n2 && intensity is { } i && powers.Count > 0
            ? powers.Count * n2 * i / (f2 * 3600.0) * 100.0
            : null;

        return new RideSummary
        {
            Ftp = validFtp,
            PowerBests = PowerCurve.Compute(series),
            ZoneSeconds = validFtp is { } zf ? PowerZones.TimeInZones(powers, zf) : null,
            IntensityFactor = intensity,
            TrainingStressScore = tss,
            StartTime = start,
            EndTime = end,
            ElapsedSeconds = elapsed,
            MovingSeconds = moving,
            DistanceMeters = samples[^1].DistanceMeters,
            SampleCount = samples.Count,
            AveragePowerWatts = powers.Count > 0 ? powers.Average() : null,
            MaxPowerWatts = samples.Max(s => s.PowerWatts),
            AverageCadenceRpm = cadences.Count > 0 ? cadences.Average() : null,
            MaxCadenceRpm = samples.Max(s => s.CadenceRpm),
            AverageSpeedMetersPerSecond = elapsed > 0 ? samples[^1].DistanceMeters / elapsed : null,
            MaxSpeedMetersPerSecond = samples.Max(s => s.SpeedMetersPerSecond),
            NormalizedPowerWatts = np,
            WorkKilojoules = powers.Count > 0 ? powers.Sum() / 1000.0 : null,
        };
    }

    public readonly record struct SecondSample(int Second, double? Power, double? Cadence, double? Speed);

    /// <summary>
    /// One entry per whole second that has data: the mean of the samples in that second. A gap between two real seconds
    /// is filled with the earlier value only when the whole gap is at most <see cref="MaxCarryForwardSeconds"/> long
    /// (a dropped packet); longer gaps are omitted entirely, and nothing is ever carried past the last real sample.
    /// Each entry counts as one full second.
    /// </summary>
    public static IReadOnlyList<SecondSample> Resample(IReadOnlyList<RideSample> samples, double elapsedSeconds)
    {
        var buckets = new SortedDictionary<int, List<RideSample>>();
        foreach (var s in samples)
        {
            if (!double.IsFinite(s.ElapsedSeconds) || s.ElapsedSeconds < 0 || s.ElapsedSeconds > elapsedSeconds + 1)
            {
                continue;
            }
            var second = (int)Math.Floor(s.ElapsedSeconds);
            if (!buckets.TryGetValue(second, out var list))
            {
                buckets[second] = list = [];
            }
            list.Add(s);
        }

        var result = new List<SecondSample>();
        SecondSample? previous = null;
        foreach (var (second, list) in buckets)
        {
            if (previous is { } prev)
            {
                var missing = second - prev.Second - 1;
                if (missing > 0 && missing <= MaxCarryForwardSeconds)
                {
                    for (var k = prev.Second + 1; k < second; k++)
                    {
                        result.Add(prev with { Second = k });
                    }
                }
            }
            var p = list.Where(x => x.PowerWatts is not null).Select(x => (double)x.PowerWatts!.Value).ToList();
            var c = list.Where(x => x.CadenceRpm is not null).Select(x => x.CadenceRpm!.Value).ToList();
            var v = list.Where(x => x.SpeedMetersPerSecond is not null).Select(x => x.SpeedMetersPerSecond!.Value).ToList();
            var entry = new SecondSample(second, p.Count > 0 ? p.Average() : null, c.Count > 0 ? c.Average() : null, v.Count > 0 ? v.Average() : null);
            result.Add(entry);
            previous = entry;
        }
        return result;
    }
}

/// <summary>
/// Normalized Power as defined by Coggan: 30-second rolling average of power, each value raised to the 4th power,
/// averaged, then the 4th root. Input is a 1 Hz series. Seconds inside long sensor gaps have already been excised
/// (not zero-filled - that would invent a stopped rider), so a window may span a drop-out, as in GoldenCheetah.
/// Returns null when fewer than 30 seconds of power exist, since the rolling window would be undefined.
/// </summary>
public static class NormalizedPowerCalculator
{
    public const int WindowSeconds = 30;

    public static double? Compute(IReadOnlyList<double> oneHzPower)
    {
        if (oneHzPower.Count < WindowSeconds)
        {
            return null;
        }
        double windowSum = 0;
        double fourthPowerSum = 0;
        var windows = 0;
        for (var i = 0; i < oneHzPower.Count; i++)
        {
            windowSum += oneHzPower[i];
            if (i >= WindowSeconds)
            {
                windowSum -= oneHzPower[i - WindowSeconds];
            }
            if (i >= WindowSeconds - 1)
            {
                var avg = windowSum / WindowSeconds;
                fourthPowerSum += avg * avg * avg * avg;
                windows++;
            }
        }
        return windows == 0 ? null : Math.Pow(fourthPowerSum / windows, 0.25);
    }
}
