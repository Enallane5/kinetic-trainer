namespace KineticTrainer.Protocol.InRide;

/// <summary>
/// GATT identifiers. Source: inRide.h (INRIDE_SERVICE_*), KineticInRide.m (KineticInRide*UUID).
/// </summary>
public static class InRideUuids
{
    public static readonly Guid PowerService = Guid.Parse("E9410100-B434-446B-B5CC-36592FC4C724");

    /// <summary>Notify, 20 bytes of (obfuscated) power data.</summary>
    public static readonly Guid PowerCharacteristic = Guid.Parse("E9410101-B434-446B-B5CC-36592FC4C724");

    /// <summary>Write with response, 3-11 bytes (README) - actually up to 15 bytes for the configure command (inRide.h).</summary>
    public static readonly Guid ControlPointCharacteristic = Guid.Parse("E9410102-B434-446B-B5CC-36592FC4C724");

    /// <summary>Read / Notify, 20 bytes of configuration.</summary>
    public static readonly Guid ConfigurationCharacteristic = Guid.Parse("E9410104-B434-446B-B5CC-36592FC4C724");

    /// <summary>Bluetooth SIG Device Information Service (0x180A).</summary>
    public static readonly Guid DeviceInformationService = Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB");

    /// <summary>System ID (0x2A23). Kinetic: 6 bytes, tied to the Nordic chip MAC; used as the command key.</summary>
    public static readonly Guid SystemIdCharacteristic = Guid.Parse("00002A23-0000-1000-8000-00805F9B34FB");

    // inRide.h: "The inRide also exposes ... FW Rev, HW Rev, Manufacturer Name". Standard SIG UUIDs.
    public static readonly Guid FirmwareRevisionCharacteristic = Guid.Parse("00002A26-0000-1000-8000-00805F9B34FB");
    public static readonly Guid HardwareRevisionCharacteristic = Guid.Parse("00002A27-0000-1000-8000-00805F9B34FB");
    public static readonly Guid ManufacturerNameCharacteristic = Guid.Parse("00002A29-0000-1000-8000-00805F9B34FB");
}

/// <summary>
/// Numeric constants copied from Kinetic's source. Each constant cites where it came from.
/// </summary>
public static class InRideConstants
{
    /// <summary>Power and configuration packets must be exactly 20 bytes (KineticInRide.m length checks).</summary>
    public const int PacketLength = 20;

    /// <summary>System ID must be exactly 6 bytes (KineticInRide.m validateSystemId).</summary>
    public const int SystemIdLength = 6;

    /// <summary>Sensor timer frequency; "ticks" are 1/32768 s (inRide.c SensorHz, inride_ticks_to_seconds).</summary>
    public const double SensorHz = 32768.0;

    // Spindown windows in seconds (inRide.c SpindownMin/Max/MinPro/MaxPro/Default).
    public const double SpindownMin = 1.5;
    public const double SpindownMax = 2.0;
    public const double SpindownMinPro = 4.7;
    public const double SpindownMaxPro = 6.5;
    public const double SpindownDefault = (SpindownMin + SpindownMax) * 0.5;

    /// <summary>
    /// inRide.c inride_speed_for_ticks: kph = 20012.256849 * revs / ticks.
    /// Equals RollerCircumferenceKm * 32768 * 3600 (KineticConstants.h), i.e. a 169.646 mm roller.
    /// </summary>
    public const double SpeedKphTicksConstant = 20012.256849;

    /// <summary>KineticConstants.h: "54mm diameter (nominal)".</summary>
    public const double RollerCircumferenceKm = 0.00016964600329384882;

    /// <summary>
    /// Roller circumference quoted in a comment in inRide.h (169.328702435836 mm). It matches the older Java SDK's
    /// speed constant (19974.826517) but NOT the C code's 20012.256849. Kept only for documentation.
    /// </summary>
    public const double RollerCircumferenceMmInRideHeaderComment = 169.328702435836;

    // Cadence calibration (inRide.c inride_process_power_data): rpm = 0.8652 * raw + 5.2617 (0 when raw == 0).
    public const double CadenceSlope = 0.8652;
    public const double CadenceIntercept = 5.2617;

    // Power model (inRide.c power_for_speed).
    public const double KphToMph = 0.621371;
    public const double PowerLinear = 5.244820;
    public const double PowerCubic = 0.019168;
    public const double DragOffsetSlope = -0.1425;
    public const double DragOffsetSlopePro = -0.021;
    public const double DragOffsetPowerSlope = 4.55;
    public const double DragOffsetPowerSlopePro = 2.62;
    public const double YIntercept = 236.20;
    public const double YInterceptPro = 104.97;

    // Coasting detection thresholds (inRide.c alpha).
    public const double CoastingAlphaThreshold = 200;
    public const double CoastingAlphaThresholdPro = 20;

    // Calibration intervals written by the configure command, in 1/32768 s ticks
    // (inRide.c inride_create_config_sensor_command_data, KineticInRide.m KineticInRideCalibrationInterval).
    public const ushort CalibrationReadyTicks = 602;
    public const ushort CalibrationStartTicks = 655;
    public const ushort CalibrationEndTicks = 950;
    public const ushort CalibrationDebounceTicks = 327;
}

/// <summary>inRide.h inride_sensor_state / KineticInRideSensorState. Bits 0x30 of the power header byte.</summary>
public enum InRideSensorState : byte
{
    Normal = 0x00,
    /// <summary>Rider needs to accelerate to over ~20 mph.</summary>
    SpindownIdle = 0x10,
    /// <summary>Rider should start coasting and NOT pedal.</summary>
    SpindownReady = 0x20,
    /// <summary>Coasting; the ~19 mph to ~11 mph interval is being timed.</summary>
    SpindownActive = 0x30,
    /// <summary>KineticInRide.h only (initial value before any packet).</summary>
    Unknown = 0xFF,
}

/// <summary>inRide.h inride_calibration_result.</summary>
public enum InRideCalibrationResult : byte
{
    Unknown = 0x00,
    Success = 0x01,
    /// <summary>Loosen the roller.</summary>
    TooFast = 0x02,
    /// <summary>Tighten the roller.</summary>
    TooSlow = 0x03,
    /// <summary>Between normal and pro-flywheel windows. Tighten without pro flywheel, loosen with it.</summary>
    Middle = 0x04,
}

/// <summary>inRide.h inride_command_result. Low nibble of the power header byte.</summary>
public enum InRideCommandResult : byte
{
    None = 0x00,
    Success = 0x01,
    NotSupported = 0x02,
    InvalidRequest = 0x03,
    CalibrationResult = 0x0A,
    UnknownError = 0x0F,
}

/// <summary>
/// inRide.h inride_update_rate / KineticInRideUpdateRate. The value is the update interval in 1/32 s
/// ("Update interval in 32Hz"): 0x20 = 32/32 s = 1 Hz, 0x10 = 2 Hz, 0x08 = 4 Hz.
/// </summary>
public enum InRideUpdateRate : ushort
{
    Millis1000 = 0x20,
    Millis500 = 0x10,
    Millis250 = 0x08,
}

/// <summary>KineticInRide.m KineticInRideSensorCommand (control point opcode, byte 2).</summary>
public enum InRideCommand : byte
{
    SetSpindownParams = 0x01,
    SetName = 0x02,
    StartCalibration = 0x03,
    StopCalibration = 0x04,
    SetSpindownTime = 0x05,
}

/// <summary>KineticInRide.m KineticInRideErrorCode.</summary>
public enum InRideErrorCode
{
    None = 0,
    InvalidSystemId = 201,
    InvalidData = 202,
    InvalidName = 203,
}

public static class InRideEnumExtensions
{
    public static bool IsDefined(this InRideCommandResult r) => Enum.IsDefined(r);

    public static string Describe(this InRideCommandResult r) => r.IsDefined() ? r.ToString() : $"Undefined(0x{(byte)r:X1})";
}

public static class InRideUpdateRateExtensions
{
    public static double Hz(this InRideUpdateRate rate) => 32.0 / (ushort)rate;

    public static TimeSpan Interval(this InRideUpdateRate rate) => TimeSpan.FromSeconds((ushort)rate / 32.0);

    public static bool IsDefined(ushort raw) => raw is (ushort)InRideUpdateRate.Millis1000 or (ushort)InRideUpdateRate.Millis500 or (ushort)InRideUpdateRate.Millis250;

    public static string Describe(ushort raw) => raw == 0
        ? "0 (invalid)"
        : $"0x{raw:X2} = {32.0 / raw:0.###} Hz ({raw * 1000.0 / 32.0:0.#} ms){(IsDefined(raw) ? string.Empty : " (not an SDK value)")}";
}
