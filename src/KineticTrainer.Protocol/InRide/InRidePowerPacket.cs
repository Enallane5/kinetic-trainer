namespace KineticTrainer.Protocol.InRide;

/// <summary>
/// One decoded Power notification: the raw fields exactly as laid out after de-obfuscation, plus every
/// derived value the Kinetic C code computes (<c>inride_power_data</c>). Nothing here depends on
/// packet history; the SDK's cadence smoothing is applied separately by <see cref="InRideCadenceSmoother"/>.
///
/// Plain (de-obfuscated) layout, all multi-byte fields little-endian:
///   [0]      header: bits 7-6 rotation, bits 5-4 <see cref="State"/>, bits 3-0 <see cref="CommandResult"/>
///   [1..3]   Interval        uint24  ticks (1/32768 s) between sensor readings (not used by the SDK maths)
///   [4..7]   Ticks           uint32  ticks spanned by <see cref="Revolutions"/> roller revolutions
///   [8]      Revolutions     uint8   roller revolutions in <see cref="Ticks"/>
///   [9..12]  TicksPrevious   uint32  same, previous reading
///   [13]     RevsPrevious    uint8
///   [14..15] CadenceRaw      uint16  crank RPM before calibration
///   [16..19] SpindownTicks   uint32  last spindown result, ticks
/// </summary>
public sealed record InRidePowerPacket
{
    // ---- raw fields ----
    public required byte[] RawBytes { get; init; }

    public required byte[] PlainBytes { get; init; }

    public required byte Rotation { get; init; }

    public required InRideSensorState State { get; init; }

    public required byte StateBits { get; init; }

    public required InRideCommandResult CommandResult { get; init; }

    public required byte CommandResultBits { get; init; }

    public required uint Interval { get; init; }

    public required uint Ticks { get; init; }

    public required byte Revolutions { get; init; }

    public required uint TicksPrevious { get; init; }

    public required byte RevolutionsPrevious { get; init; }

    public required ushort CadenceRaw { get; init; }

    public required uint SpindownTicks { get; init; }

    // ---- derived (inride_power_data) ----

    /// <summary>inride_power_data.power. 0 while coasting.</summary>
    public required int PowerWatts { get; init; }

    /// <summary>inride_power_data.speedKPH (roller surface speed).</summary>
    public required double SpeedKph { get; init; }

    public required double RollerRpm { get; init; }

    /// <summary>Calibrated but unsmoothed cadence: 0.8652 * raw + 5.2617 (inride_power_data.cadenceRPM).</summary>
    public required double CadenceCalibratedRpm { get; init; }

    public required bool Coasting { get; init; }

    /// <summary>Acceleration term from inRide.c <c>alpha()</c>; drives coasting detection.</summary>
    public required double Alpha { get; init; }

    /// <summary>Spindown time (s) the power model used: default 1.75 unless the last result is in a valid window.</summary>
    public required double SpindownTimeSeconds { get; init; }

    public required double LastSpindownResultSeconds { get; init; }

    /// <summary>Normalised spindown (0..1) within the applicable window.</summary>
    public required double RollerResistance { get; init; }

    public required bool ProFlywheel { get; init; }

    public required InRideCalibrationResult CalibrationResult { get; init; }

    public double SpeedKphPrevious { get; init; }

    public string RawHex => Convert.ToHexStringLower(RawBytes);

    public string PlainHex => Convert.ToHexStringLower(PlainBytes);
}

/// <summary>Decoded Configuration characteristic (<c>inride_config_data</c>).</summary>
/// <remarks>
/// Layout (little-endian): [0..1] calibrationReady, [2..3] calibrationStart, [4..5] calibrationEnd,
/// [6..7] calibrationDebounce (all uint16 ticks), [8..11] currentSpindownTicks uint32, [12..13] updateRateDefault
/// uint16, [14..15] updateRateCalibration uint16, [16..19] not interpreted by the SDK.
/// </remarks>
public sealed record InRideConfiguration
{
    public required byte[] RawBytes { get; init; }

    public required ushort CalibrationReadyTicks { get; init; }

    public required ushort CalibrationStartTicks { get; init; }

    public required ushort CalibrationEndTicks { get; init; }

    public required ushort CalibrationDebounceTicks { get; init; }

    public required uint CurrentSpindownTicks { get; init; }

    public required double CurrentSpindownSeconds { get; init; }

    /// <summary>Raw update-interval value (expected 0x20 / 0x10 / 0x08 = 1 / 2 / 4 Hz).</summary>
    public required ushort UpdateRateDefaultRaw { get; init; }

    public required ushort UpdateRateCalibrationRaw { get; init; }

    public required bool ProFlywheel { get; init; }

    /// <summary>Bytes 16-19, which Kinetic's code ignores. Logged for validation against the real sensor.</summary>
    public required byte[] UnusedTail { get; init; }

    public InRideUpdateRate? UpdateRateDefault =>
        InRideUpdateRateExtensions.IsDefined(UpdateRateDefaultRaw) ? (InRideUpdateRate)UpdateRateDefaultRaw : null;

    public string RawHex => Convert.ToHexStringLower(RawBytes);

    public override string ToString() =>
        $"spindown={CurrentSpindownSeconds:0.000}s ({CurrentSpindownTicks} ticks) pro={ProFlywheel} " +
        $"rate={InRideUpdateRateExtensions.Describe(UpdateRateDefaultRaw)} calRate={InRideUpdateRateExtensions.Describe(UpdateRateCalibrationRaw)} " +
        $"calReady={CalibrationReadyTicks} calStart={CalibrationStartTicks} calEnd={CalibrationEndTicks} calDebounce={CalibrationDebounceTicks} " +
        $"tail={Convert.ToHexStringLower(UnusedTail)}";
}
