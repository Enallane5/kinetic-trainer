namespace KineticTrainer.Workouts.Model;

/// <summary>
/// Step types. They map one-to-one onto ZWO elements so a workout survives import → edit → export unchanged:
/// Warmup/Cooldown/Ramp = ramped power, SteadyState = flat, Intervals = IntervalsT, FreeRide, MaxEffort.
/// "Recovery" is not a separate element in ZWO; recovery is a <see cref="SegmentRole"/> derived from intensity.
/// </summary>
public enum WorkoutStepKind
{
    Warmup,
    SteadyState,
    Ramp,
    Cooldown,
    Intervals,
    FreeRide,
    MaxEffort,
}

/// <summary>How the power numbers in a workout are expressed.</summary>
public enum WorkoutPowerUnit
{
    /// <summary>Fractions of FTP (1.0 = 100 %). This is what ZWO files use.</summary>
    FtpFraction,

    /// <summary>Absolute watts (for the builder; exported to ZWO by dividing by the rider's FTP).</summary>
    Watts,
}

/// <summary>A text message shown (and optionally spoken) at an offset from the start of its step.</summary>
public sealed record WorkoutTextEvent(int OffsetSeconds, string Message);

/// <summary>
/// One step of a structured workout. Power values are in the workout's <see cref="WorkoutPowerUnit"/>.
/// For ramps, <see cref="PowerLow"/> is the power at the start of the step and <see cref="PowerHigh"/> at the end
/// (so a cool-down has PowerLow &gt; PowerHigh). For flat steps both are equal.
/// </summary>
public sealed record WorkoutStep
{
    public required WorkoutStepKind Kind { get; init; }

    /// <summary>Length of a non-interval step, whole seconds.</summary>
    public int DurationSeconds { get; init; }

    public double PowerLow { get; init; }
    public double PowerHigh { get; init; }

    /// <summary>Interval repeats (Intervals only).</summary>
    public int Repeat { get; init; } = 1;

    public int OnDurationSeconds { get; init; }
    public int OffDurationSeconds { get; init; }
    public double OnPowerLow { get; init; }
    public double OnPowerHigh { get; init; }
    public double OffPowerLow { get; init; }
    public double OffPowerHigh { get; init; }

    /// <summary>Cadence target, rpm (the on-phase for intervals). Display only.</summary>
    public int? Cadence { get; init; }

    /// <summary>Cadence target for interval off-phases, rpm.</summary>
    public int? CadenceResting { get; init; }

    public int? CadenceLow { get; init; }
    public int? CadenceHigh { get; init; }

    /// <summary>Text events relative to the start of this step (for intervals: the start of the whole block).</summary>
    public IReadOnlyList<WorkoutTextEvent> TextEvents { get; init; } = [];

    /// <summary>Total length of this step including all repeats.</summary>
    public int TotalSeconds => Kind == WorkoutStepKind.Intervals
        ? Math.Max(1, Repeat) * (OnDurationSeconds + OffDurationSeconds)
        : DurationSeconds;

    public bool HasPowerTarget => Kind is not (WorkoutStepKind.FreeRide or WorkoutStepKind.MaxEffort);

    public static WorkoutStep Steady(int seconds, double power, int? cadence = null) =>
        new() { Kind = WorkoutStepKind.SteadyState, DurationSeconds = seconds, PowerLow = power, PowerHigh = power, Cadence = cadence };

    public static WorkoutStep Ramp(WorkoutStepKind kind, int seconds, double from, double to) =>
        new() { Kind = kind, DurationSeconds = seconds, PowerLow = from, PowerHigh = to };

    public static WorkoutStep Intervals(int repeat, int onSeconds, double onPower, int offSeconds, double offPower, int? cadence = null, int? cadenceResting = null) =>
        new()
        {
            Kind = WorkoutStepKind.Intervals,
            Repeat = repeat,
            OnDurationSeconds = onSeconds,
            OffDurationSeconds = offSeconds,
            OnPowerLow = onPower,
            OnPowerHigh = onPower,
            OffPowerLow = offPower,
            OffPowerHigh = offPower,
            Cadence = cadence,
            CadenceResting = cadenceResting,
        };

    public static WorkoutStep FreeRide(int seconds) => new() { Kind = WorkoutStepKind.FreeRide, DurationSeconds = seconds };

    public static WorkoutStep MaxEffort(int seconds) => new() { Kind = WorkoutStepKind.MaxEffort, DurationSeconds = seconds };
}

/// <summary>Which FTP test protocol a workout implements (drives the estimate at the end of the ride).</summary>
public enum FtpTestKind
{
    None,

    /// <summary>Allen/Coggan: FTP = 95 % of the average power of the 20-minute effort.</summary>
    TwentyMinute,

    /// <summary>Ramp: FTP = 75 % of the best 1-minute power in the ramp.</summary>
    Ramp,

    /// <summary>CTS: FTP = 90 % of the better of two 8-minute efforts.</summary>
    EightMinute,
}

/// <summary>A structured workout.</summary>
public sealed record Workout
{
    public required string Name { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }

    /// <summary>Primary category (first tag), e.g. "Sweet Spot".</summary>
    public string? Category => Tags.Count > 0 ? Tags[0] : null;

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<WorkoutStep> Steps { get; init; } = [];
    public WorkoutPowerUnit PowerUnit { get; init; } = WorkoutPowerUnit.FtpFraction;

    /// <summary>Text events placed directly under &lt;workout&gt;, relative to the workout start.</summary>
    public IReadOnlyList<WorkoutTextEvent> WorkoutTextEvents { get; init; } = [];

    public FtpTestKind FtpTest { get; init; } = FtpTestKind.None;

    public int TotalSeconds => Steps.Sum(s => s.TotalSeconds);

    /// <summary>Tag written to ZWO files to mark the FTP-test protocol so a re-imported test still estimates FTP.</summary>
    public static string FtpTestTag(FtpTestKind kind) => kind switch
    {
        FtpTestKind.TwentyMinute => "FTP Test 20 min",
        FtpTestKind.Ramp => "FTP Test Ramp",
        FtpTestKind.EightMinute => "FTP Test 8 min",
        _ => "",
    };

    public static FtpTestKind FtpTestFromTags(IEnumerable<string> tags)
    {
        foreach (var t in tags)
        {
            foreach (var kind in new[] { FtpTestKind.TwentyMinute, FtpTestKind.Ramp, FtpTestKind.EightMinute })
            {
                if (string.Equals(t.Trim(), FtpTestTag(kind), StringComparison.OrdinalIgnoreCase))
                {
                    return kind;
                }
            }
        }
        return FtpTestKind.None;
    }

    /// <summary>Converts to FTP fractions (for export) using the given FTP when the workout is in watts.</summary>
    public Workout ToFtpFractions(int ftp)
    {
        if (PowerUnit == WorkoutPowerUnit.FtpFraction)
        {
            return this;
        }
        if (ftp <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ftp), "An FTP is needed to convert watts to FTP fractions.");
        }
        double F(double w) => Math.Round(w / ftp, 3);
        return this with
        {
            PowerUnit = WorkoutPowerUnit.FtpFraction,
            Steps = Steps.Select(s => s with
            {
                PowerLow = F(s.PowerLow),
                PowerHigh = F(s.PowerHigh),
                OnPowerLow = F(s.OnPowerLow),
                OnPowerHigh = F(s.OnPowerHigh),
                OffPowerLow = F(s.OffPowerLow),
                OffPowerHigh = F(s.OffPowerHigh),
            }).ToArray(),
        };
    }
}
