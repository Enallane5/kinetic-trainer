namespace KineticTrainer.Core.Rides;

/// <summary>One recorded telemetry sample. Only samples with valid sensor data are recorded; gaps stay gaps.</summary>
public sealed record RideSample(
    DateTimeOffset Timestamp,
    /// <summary>Seconds since the ride timer started.</summary>
    double ElapsedSeconds,
    int? PowerWatts,
    double? CadenceRpm,
    double? SpeedMetersPerSecond,
    /// <summary>Cumulative distance at this sample, metres.</summary>
    double DistanceMeters);

/// <summary>Derived statistics for a ride. All SI. Nulls mean "no data".</summary>
public sealed record RideSummary
{
    public required DateTimeOffset StartTime { get; init; }

    public required DateTimeOffset EndTime { get; init; }

    /// <summary>Wall-clock seconds from start to stop (the ride timer keeps running through sensor drop-outs).</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>Seconds with sensor data and speed > 0.</summary>
    public required double MovingSeconds { get; init; }

    public required double DistanceMeters { get; init; }

    public int SampleCount { get; init; }

    /// <summary>Mean of the 1 Hz power series (seconds with data, short gaps carried).</summary>
    public double? AveragePowerWatts { get; init; }

    public int? MaxPowerWatts { get; init; }

    public double? AverageCadenceRpm { get; init; }

    public double? MaxCadenceRpm { get; init; }

    /// <summary>Distance / elapsed (wall-clock) time - the FIT/Garmin meaning. Sensor drop-outs lower it slightly.</summary>
    public double? AverageSpeedMetersPerSecond { get; init; }

    public double? MaxSpeedMetersPerSecond { get; init; }

    /// <summary>Coggan Normalized Power (30 s rolling average, 4th-power mean). Null when the ride is shorter than 30 s of data.</summary>
    public double? NormalizedPowerWatts { get; init; }

    /// <summary>Total mechanical work in kilojoules (integral of power).</summary>
    public double? WorkKilojoules { get; init; }

    /// <summary>FTP used for the FTP-relative figures below (the rider's FTP when the ride started).</summary>
    public int? Ftp { get; init; }

    /// <summary>Seconds in each Coggan zone, index 0 = Z1. Null when FTP was unknown.</summary>
    public IReadOnlyList<double>? ZoneSeconds { get; init; }

    /// <summary>Intensity Factor = NP / FTP.</summary>
    public double? IntensityFactor { get; init; }

    /// <summary>
    /// Training Stress Score computed with the standard formula (duration × NP × IF) / (FTP × 3600) × 100.
    /// Only present when both FTP and Normalized Power are known.
    /// </summary>
    public double? TrainingStressScore { get; init; }

    /// <summary>Best average power per duration (seconds -> watts), contiguous data only.</summary>
    public IReadOnlyDictionary<int, double>? PowerBests { get; init; }
}
