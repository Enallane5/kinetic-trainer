using KineticTrainer.Core.Units;

namespace KineticTrainer.Core.Settings;

/// <summary>Sensor update rate the rider can request (inRide supports 1, 2 and 4 Hz).</summary>
public enum SensorUpdateRate
{
    OneHz = 1,
    TwoHz = 2,
    FourHz = 4,
}

/// <summary>
/// Bluetooth LE address type as seen in the advertisement. Windows needs it to open the right device object:
/// the inRide uses a random static address (first byte 0xC0-0xFF).
/// </summary>
public enum SensorAddressType
{
    Unspecified = 0,
    Public = 1,
    Random = 2,
}

/// <summary>The sensor that was last connected successfully; used for automatic reconnection.</summary>
public sealed record SavedSensor(ulong BluetoothAddress, string? Name, string? SystemIdHex, DateTimeOffset LastConnectedUtc,
    SensorAddressType AddressType = SensorAddressType.Unspecified);

/// <summary>All locally stored application settings. Immutable; save a modified copy.</summary>
public sealed record AppSettings
{
    /// <summary>
    /// Kinetic roller circumference in metres. Default is Kinetic's own constant
    /// <c>RollerCircumferenceKM = 0.00016964600329384882</c> (KineticConstants.h, "54mm diameter (nominal)"),
    /// i.e. pi x 54 mm = 169.646 mm. It is also the value implied by inRide.c's speed constant 20012.256849.
    /// </summary>
    public const double KineticRollerCircumferenceMeters = 0.16964600329384882;

    public const int DefaultBridgePort = 47474;

    public double RollerCircumferenceMeters { get; init; } = KineticRollerCircumferenceMeters;

    public SpeedUnit SpeedUnit { get; init; } = SpeedUnit.MilesPerHour;

    public DistanceUnit DistanceUnit { get; init; } = DistanceUnit.Miles;

    /// <summary>Update rate to apply to the sensor on connect. Null = leave the sensor's current rate alone.</summary>
    public SensorUpdateRate? PreferredUpdateRate { get; init; }

    public SavedSensor? LastSensor { get; init; }

    public bool AutoReconnect { get; init; } = true;

    /// <summary>Folder for exported FIT/TCX files. Null = Documents\KineticTrainer\Rides.</summary>
    public string? ExportFolder { get; init; }

    /// <summary>The rider profile in use. FTP and body metrics live on the profile, not here.</summary>
    public long? ActiveProfileId { get; init; }

    /// <summary>Play audio cues during workouts (tones generated locally; no network).</summary>
    public bool AudioCuesEnabled { get; init; } = true;

    /// <summary>Also speak workout cues with the Windows offline voice.</summary>
    public bool SpokenCuesEnabled { get; init; } = true;

    /// <summary>"On target" band around a workout target, percent of the target (minimum 5 W either side).</summary>
    public int TargetTolerancePercent { get; init; } = 5;

    /// <summary>Width of the live rolling power graph, minutes (1, 5, 10 or 20).</summary>
    public int PowerGraphMinutes { get; init; } = 5;

    public static readonly IReadOnlyList<int> PowerGraphChoices = [1, 5, 10, 20];

    /// <summary>Colour workout profile bars by power zone (off = Kinetic Fit's single green gradient).</summary>
    public bool ColorProfilesByZone { get; init; } = true;

    /// <summary>Show a large pop-up with the next target when an interval starts.</summary>
    public bool ShowIntervalPopups { get; init; } = true;

    public bool SimulatorBridgeEnabled { get; init; } = true;

    public int SimulatorBridgePort { get; init; } = DefaultBridgePort;

    /// <summary>Write the diagnostic/packet log to disk (for validating against the physical sensor).</summary>
    public bool WriteDiagnosticLogFile { get; init; } = true;

    /// <summary>Show a clearly labelled simulated sensor in the device list (development aid, no hardware needed).</summary>
    public bool ShowSimulatedSensor { get; init; }

    /// <summary>
    /// Diagnostic override. Kinetic's SDK rejects power packets unless the Device Information System ID is
    /// exactly 6 bytes, even though the decoding itself never uses it. Leave off unless the debug view shows
    /// the physical sensor reporting a different System ID length.
    /// </summary>
    public bool IgnoreSystemIdValidation { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!double.IsFinite(RollerCircumferenceMeters) || RollerCircumferenceMeters is < 0.05 or > 1.0)
        {
            errors.Add("Roller circumference must be between 50 mm and 1000 mm.");
        }
        if (SimulatorBridgePort is < 1024 or > 65535)
        {
            errors.Add("Simulator bridge port must be between 1024 and 65535.");
        }
        if (TargetTolerancePercent is < 1 or > 25)
        {
            errors.Add("Target tolerance must be between 1 and 25 %.");
        }
        if (!PowerGraphChoices.Contains(PowerGraphMinutes))
        {
            errors.Add("Power graph duration must be 1, 5, 10 or 20 minutes.");
        }
        return errors;
    }
}
