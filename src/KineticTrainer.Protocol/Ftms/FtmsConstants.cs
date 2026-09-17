namespace KineticTrainer.Protocol.Ftms;

/// <summary>
/// Bluetooth SIG Fitness Machine Service (FTMS) identifiers.
/// The Kinetic sensor validated on 2026-09-15 (Manufacturer "Kinetic", Model "inRide", HW 10, FW 1.2) exposes
/// this standard service instead of Kinetic's custom inRide 2 service - see docs/PROTOCOL.md.
/// </summary>
public static class FtmsUuids
{
    private static Guid Sig(ushort id) => new((uint)id, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);

    /// <summary>0x1826 Fitness Machine Service.</summary>
    public static readonly Guid FitnessMachineService = Sig(0x1826);

    /// <summary>0x2ACC Fitness Machine Feature (read).</summary>
    public static readonly Guid FitnessMachineFeature = Sig(0x2ACC);

    /// <summary>0x2AD2 Indoor Bike Data (notify) - power, cadence, speed.</summary>
    public static readonly Guid IndoorBikeData = Sig(0x2AD2);

    /// <summary>0x2ADA Fitness Machine Status (notify) - spin-down progress and target changes.</summary>
    public static readonly Guid FitnessMachineStatus = Sig(0x2ADA);

    /// <summary>0x2AD9 Fitness Machine Control Point (write + indicate).</summary>
    public static readonly Guid FitnessMachineControlPoint = Sig(0x2AD9);

    /// <summary>0x2AD3 Training Status (optional).</summary>
    public static readonly Guid TrainingStatus = Sig(0x2AD3);

    /// <summary>0x2AD8 Supported Power Range (optional).</summary>
    public static readonly Guid SupportedPowerRange = Sig(0x2AD8);

    /// <summary>0x2AD6 Supported Resistance Level Range (optional).</summary>
    public static readonly Guid SupportedResistanceLevelRange = Sig(0x2AD6);

    /// <summary>0x180F Battery Service.</summary>
    public static readonly Guid BatteryService = Sig(0x180F);

    /// <summary>0x2A19 Battery Level.</summary>
    public static readonly Guid BatteryLevel = Sig(0x2A19);

    /// <summary>0x2A24 Model Number String.</summary>
    public static readonly Guid ModelNumber = Sig(0x2A24);
}

/// <summary>Indoor Bike Data flags (0x2AD2, first 2 bytes, little-endian).</summary>
[Flags]
public enum IndoorBikeDataFlags : ushort
{
    /// <summary>
    /// Bit 0 is "More Data" and is INVERTED with respect to the other bits: when it is CLEAR the
    /// Instantaneous Speed field is present; when SET the field is absent.
    /// </summary>
    MoreData = 1 << 0,
    AverageSpeed = 1 << 1,
    InstantaneousCadence = 1 << 2,
    AverageCadence = 1 << 3,
    TotalDistance = 1 << 4,
    ResistanceLevel = 1 << 5,
    InstantaneousPower = 1 << 6,
    AveragePower = 1 << 7,
    ExpendedEnergy = 1 << 8,
    HeartRate = 1 << 9,
    MetabolicEquivalent = 1 << 10,
    ElapsedTime = 1 << 11,
    RemainingTime = 1 << 12,
}

/// <summary>Fitness Machine Feature bits, first uint32 of 0x2ACC.</summary>
[Flags]
public enum FitnessMachineFeatures : uint
{
    AverageSpeed = 1u << 0,
    Cadence = 1u << 1,
    TotalDistance = 1u << 2,
    Inclination = 1u << 3,
    ElevationGain = 1u << 4,
    Pace = 1u << 5,
    StepCount = 1u << 6,
    ResistanceLevel = 1u << 7,
    StrideCount = 1u << 8,
    ExpendedEnergy = 1u << 9,
    HeartRateMeasurement = 1u << 10,
    MetabolicEquivalent = 1u << 11,
    ElapsedTime = 1u << 12,
    RemainingTime = 1u << 13,
    PowerMeasurement = 1u << 14,
    ForceOnBeltAndPowerOutput = 1u << 15,
    UserDataRetention = 1u << 16,
}

/// <summary>Target Setting Feature bits, second uint32 of 0x2ACC.</summary>
[Flags]
public enum TargetSettingFeatures : uint
{
    SpeedTarget = 1u << 0,
    InclinationTarget = 1u << 1,
    ResistanceTarget = 1u << 2,
    PowerTarget = 1u << 3,
    HeartRateTarget = 1u << 4,
    TargetedExpendedEnergy = 1u << 5,
    TargetedStepNumber = 1u << 6,
    TargetedStrideNumber = 1u << 7,
    TargetedDistance = 1u << 8,
    TargetedTrainingTime = 1u << 9,
    TargetedTimeInTwoHeartRateZones = 1u << 10,
    TargetedTimeInThreeHeartRateZones = 1u << 11,
    TargetedTimeInFiveHeartRateZones = 1u << 12,
    IndoorBikeSimulationParameters = 1u << 13,
    WheelCircumferenceConfiguration = 1u << 14,
    SpinDownControl = 1u << 15,
    TargetedCadenceConfiguration = 1u << 16,
}

/// <summary>Control Point (0x2AD9) op codes.</summary>
public enum FtmsOpCode : byte
{
    RequestControl = 0x00,
    Reset = 0x01,
    SetTargetSpeed = 0x02,
    SetTargetInclination = 0x03,
    SetTargetResistanceLevel = 0x04,
    SetTargetPower = 0x05,
    SetTargetHeartRate = 0x06,
    StartOrResume = 0x07,
    StopOrPause = 0x08,
    SetTargetedExpendedEnergy = 0x09,
    SetTargetedNumberOfSteps = 0x0A,
    SetTargetedNumberOfStrides = 0x0B,
    SetTargetedDistance = 0x0C,
    SetTargetedTrainingTime = 0x0D,
    SetTargetedTimeInTwoHeartRateZones = 0x0E,
    SetTargetedTimeInThreeHeartRateZones = 0x0F,
    SetTargetedTimeInFiveHeartRateZones = 0x10,
    SetIndoorBikeSimulationParameters = 0x11,
    SetWheelCircumference = 0x12,
    SpinDownControl = 0x13,
    SetTargetedCadence = 0x14,
    ResponseCode = 0x80,
}

/// <summary>Result code in a Control Point response (0x80 requestOpCode resultCode ...).</summary>
public enum FtmsResultCode : byte
{
    Reserved = 0x00,
    Success = 0x01,
    OpCodeNotSupported = 0x02,
    InvalidParameter = 0x03,
    OperationFailed = 0x04,
    ControlNotPermitted = 0x05,
}

/// <summary>Parameter of <see cref="FtmsOpCode.SpinDownControl"/>.</summary>
public enum SpinDownControlParameter : byte
{
    Start = 0x01,
    Ignore = 0x02,
}

/// <summary>Fitness Machine Status (0x2ADA) op codes.</summary>
public enum FtmsStatusOpCode : byte
{
    Reset = 0x01,
    StoppedOrPausedByUser = 0x02,
    StoppedBySafetyKey = 0x03,
    StartedOrResumedByUser = 0x04,
    TargetSpeedChanged = 0x05,
    TargetInclineChanged = 0x06,
    TargetResistanceLevelChanged = 0x07,
    TargetPowerChanged = 0x08,
    TargetHeartRateChanged = 0x09,
    TargetedExpendedEnergyChanged = 0x0A,
    TargetedNumberOfStepsChanged = 0x0B,
    TargetedNumberOfStridesChanged = 0x0C,
    TargetedDistanceChanged = 0x0D,
    TargetedTrainingTimeChanged = 0x0E,
    TargetedTimeInTwoHeartRateZonesChanged = 0x0F,
    TargetedTimeInThreeHeartRateZonesChanged = 0x10,
    TargetedTimeInFiveHeartRateZonesChanged = 0x11,
    IndoorBikeSimulationParametersChanged = 0x12,
    WheelCircumferenceChanged = 0x13,
    SpinDownStatus = 0x14,
    TargetedCadenceChanged = 0x15,
    ControlPermissionLost = 0xFF,
}

/// <summary>Parameter of <see cref="FtmsStatusOpCode.SpinDownStatus"/> - the calibration state machine.</summary>
public enum SpinDownStatus : byte
{
    SpinDownRequested = 0x01,
    Success = 0x02,
    Error = 0x03,
    StopPedaling = 0x04,
}
