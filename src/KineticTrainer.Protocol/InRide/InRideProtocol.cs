namespace KineticTrainer.Protocol.InRide;

/// <summary>The fully processed result of one Power notification, as the Kinetic SDK would hand it to an app.</summary>
public sealed record InRidePowerData(InRidePowerPacket Packet, double CadenceRpm, double TimestampSeconds);

/// <summary>
/// Validated entry points mirroring the public <c>KineticInRide</c> class (KineticInRide.m): the same length and
/// System ID checks with the same error codes, then the C parser, then the SDK cadence adjustment.
/// One instance per sensor connection (it owns the cadence smoothing state).
/// </summary>
public sealed class InRideProtocol
{
    private readonly InRideCadenceSmoother _cadence;

    public InRideProtocol(InRideCadenceSmoother? cadenceSmoother = null)
    {
        _cadence = cadenceSmoother ?? new InRideCadenceSmoother();
    }

    /// <summary>
    /// <c>processPowerData:systemId:error:</c>. Rejects packets that are not 20 bytes (202) and, unless
    /// <paramref name="ignoreSystemIdValidation"/>, a System ID that is not 6 bytes (201). Note the decoding itself
    /// never uses the System ID; Kinetic validates it here only because commands need it.
    /// </summary>
    public InRideResult<InRidePowerData> ProcessPowerData(ReadOnlySpan<byte> data, ReadOnlySpan<byte> systemId, double timestampSeconds,
        bool ignoreSystemIdValidation = false)
    {
        if (data.Length != InRideConstants.PacketLength)
        {
            return InRideResult<InRidePowerData>.Fail(InRideErrorCode.InvalidData,
                $"Invalid inRide Power Data: expected {InRideConstants.PacketLength} bytes, got {data.Length}");
        }
        if (!ignoreSystemIdValidation && systemId.Length != InRideConstants.SystemIdLength)
        {
            return InRideResult<InRidePowerData>.Fail(InRideErrorCode.InvalidSystemId,
                $"Invalid System ID: expected {InRideConstants.SystemIdLength} bytes, got {systemId.Length}");
        }

        var packet = InRidePowerParser.Parse(data);
        var cadence = _cadence.Adjust(packet.CadenceCalibratedRpm, timestampSeconds);
        return InRideResult<InRidePowerData>.Ok(new InRidePowerData(packet, cadence, timestampSeconds));
    }

    /// <summary><c>processConfigurationData:error:</c>. Rejects packets that are not 20 bytes (202).</summary>
    public static InRideResult<InRideConfiguration> ProcessConfigurationData(ReadOnlySpan<byte> data)
    {
        if (data.Length != InRideConstants.PacketLength)
        {
            return InRideResult<InRideConfiguration>.Fail(InRideErrorCode.InvalidData,
                $"Invalid inRide Config Data: expected {InRideConstants.PacketLength} bytes, got {data.Length}");
        }
        return InRideResult<InRideConfiguration>.Ok(InRideConfigurationParser.Parse(data));
    }

    /// <summary><c>configureSensorCommandData:updateRate:error:</c></summary>
    public static InRideResult<byte[]> ConfigureSensorCommandData(ReadOnlySpan<byte> systemId, InRideUpdateRate rate) =>
        WithSystemId(systemId, id => InRideCommands.ConfigureSensor(id, rate));

    /// <summary><c>startCalibrationCommandData:error:</c></summary>
    public static InRideResult<byte[]> StartCalibrationCommandData(ReadOnlySpan<byte> systemId) =>
        WithSystemId(systemId, InRideCommands.StartCalibration);

    /// <summary><c>stopCalibrationCommandData:error:</c></summary>
    public static InRideResult<byte[]> StopCalibrationCommandData(ReadOnlySpan<byte> systemId) =>
        WithSystemId(systemId, InRideCommands.StopCalibration);

    /// <summary><c>setSpindownTimeCommandData:seconds:error:</c></summary>
    public static InRideResult<byte[]> SetSpindownTimeCommandData(ReadOnlySpan<byte> systemId, double seconds) =>
        WithSystemId(systemId, id => InRideCommands.SetSpindownTime(id, seconds));

    /// <summary><c>setPeripheralNameCommandData:name:error:</c> (203 for a bad name).</summary>
    public static InRideResult<byte[]> SetPeripheralNameCommandData(ReadOnlySpan<byte> systemId, string name)
    {
        if (systemId.Length != InRideConstants.SystemIdLength)
        {
            return InRideResult<byte[]>.Fail(InRideErrorCode.InvalidSystemId, "Invalid System ID");
        }
        if (name is null || name.Length < 3 || name.Length > 8)
        {
            return InRideResult<byte[]>.Fail(InRideErrorCode.InvalidName, "Invalid Sensor Name. Must be between 3 and 8 characters.");
        }
        return InRideResult<byte[]>.Ok(InRideCommands.SetPeripheralName(systemId, name));
    }

    /// <summary>
    /// <c>calibrationReadySpeedKPH</c>: the roller speed at which the sensor leaves Spindown Idle
    /// (speed for one revolution per 602 ticks = 33.2 km/h = 20.7 mph).
    /// </summary>
    public static double CalibrationReadySpeedKph => InRidePowerParser.SpeedKphForTicks(InRideConstants.CalibrationReadyTicks, 1);

    /// <summary>Speed at which the timed spindown window starts (655 ticks/rev = 30.6 km/h = 19.0 mph).</summary>
    public static double CalibrationStartSpeedKph => InRidePowerParser.SpeedKphForTicks(InRideConstants.CalibrationStartTicks, 1);

    /// <summary>Speed at which the timed spindown window ends (950 ticks/rev = 21.1 km/h = 13.1 mph).</summary>
    public static double CalibrationEndSpeedKph => InRidePowerParser.SpeedKphForTicks(InRideConstants.CalibrationEndTicks, 1);

    /// <summary><c>+[KineticSDK systemIdToString:]</c>: lowercase hex, no separators.</summary>
    public static string SystemIdToString(ReadOnlySpan<byte> systemId) => Convert.ToHexStringLower(systemId);

    public void ResetCadenceSmoothing() => _cadence.Reset();

    private delegate byte[] CommandBuilder(ReadOnlySpan<byte> systemId);

    private static InRideResult<byte[]> WithSystemId(ReadOnlySpan<byte> systemId, CommandBuilder build)
    {
        if (systemId.Length != InRideConstants.SystemIdLength)
        {
            return InRideResult<byte[]>.Fail(InRideErrorCode.InvalidSystemId, "Invalid System ID");
        }
        try
        {
            return InRideResult<byte[]>.Ok(build(systemId));
        }
        catch (ArgumentException ex)
        {
            return InRideResult<byte[]>.Fail(InRideErrorCode.InvalidData, ex.Message);
        }
    }
}
