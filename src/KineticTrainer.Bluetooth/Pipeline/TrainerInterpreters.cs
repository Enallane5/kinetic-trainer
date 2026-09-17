using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Core.Units;
using KineticTrainer.Protocol.Ftms;
using KineticTrainer.Protocol.InRide;

namespace KineticTrainer.Bluetooth.Pipeline;

/// <summary>One primary data notification after interpretation, in a protocol-neutral shape plus the protocol-specific detail.</summary>
public sealed record InterpretedPacket
{
    public required bool Accepted { get; init; }

    public string? RejectReason { get; init; }

    public required byte[] RawBytes { get; init; }

    public int? PowerWatts { get; init; }

    public double? CadenceRpm { get; init; }

    public double? RollerRpm { get; init; }

    public double? SpeedMetersPerSecond { get; init; }

    public bool? Coasting { get; init; }

    public TrainerSensorMode Mode { get; init; } = TrainerSensorMode.Normal;

    /// <summary>One line for the event log.</summary>
    public required string Summary { get; init; }

    /// <summary>inRide: the de-obfuscated bytes. FTMS packets are not obfuscated, so null.</summary>
    public string? DecodedHex { get; init; }

    public string StateText { get; init; } = "";

    public string CommandText { get; init; } = "";

    public string Detail { get; init; } = "";

    public InRidePowerData? InRide { get; init; }

    public IndoorBikeData? Ftms { get; init; }

    public string RawHex => Convert.ToHexStringLower(RawBytes);
}

/// <summary>Result of interpreting a configuration read or a status notification. <paramref name="Payload"/> carries the typed event when there is one.</summary>
public sealed record StatusInterpretation(bool Accepted, string Text, string? Warning = null, object? Payload = null);

/// <summary>Turns raw characteristic bytes into telemetry for one protocol. One instance per connection.</summary>
public interface ITrainerInterpreter
{
    TrainerProtocolKind Kind { get; }

    string Name { get; }

    /// <summary>Metres per roller revolution (inRide speed derivation only; FTMS reports speed directly).</summary>
    double RollerCircumferenceMeters { get; set; }

    InterpretedPacket InterpretData(byte[] data, DateTimeOffset at);

    /// <summary>The characteristic read after connecting: inRide Configuration, or FTMS Fitness Machine Feature.</summary>
    StatusInterpretation InterpretConfiguration(byte[] data);

    /// <summary>Secondary notifications: inRide Configuration, or FTMS Fitness Machine Status.</summary>
    StatusInterpretation InterpretStatus(byte[] data);

    /// <summary>FTMS control point indication. Returns null when the protocol has none.</summary>
    StatusInterpretation? InterpretControlResponse(byte[] data);

    /// <summary>Multi-line description of the last configuration/feature for the Sensor page, or null.</summary>
    string? DescribeLatestConfiguration();

    void Reset();
}

/// <summary>Kinetic inRide 2 custom protocol (port of Kinetic's SDK).</summary>
public sealed class InRideInterpreter : ITrainerInterpreter
{
    private readonly InRideProtocol _protocol = new();
    private readonly object _gate = new();

    public TrainerProtocolKind Kind => TrainerProtocolKind.InRideCustom;

    public string Name => "Kinetic inRide 2 (custom E9410100)";

    public double RollerCircumferenceMeters { get; set; } = Core.Settings.AppSettings.KineticRollerCircumferenceMeters;

    public byte[]? SystemId { get; set; }

    public bool IgnoreSystemIdValidation { get; set; }

    public InRideConfiguration? LatestConfiguration { get; private set; }

    public InterpretedPacket InterpretData(byte[] data, DateTimeOffset at)
    {
        InRideResult<InRidePowerData> result;
        lock (_gate)
        {
            result = _protocol.ProcessPowerData(data, SystemId ?? [], at.ToUnixTimeMilliseconds() / 1000.0, IgnoreSystemIdValidation);
        }
        if (!result.IsSuccess)
        {
            return new InterpretedPacket
            {
                Accepted = false,
                RawBytes = data,
                RejectReason = $"{result.Error}/{(int)result.Error}: {result.Message}",
                Summary = $"rejected ({result.Error}/{(int)result.Error}): {result.Message}",
            };
        }

        var power = result.Value;
        var p = power.Packet;
        var speedMps = UnitConversions.RollerSpeedMetersPerSecond(p.RollerRpm, RollerCircumferenceMeters);
        return new InterpretedPacket
        {
            Accepted = true,
            RawBytes = data,
            PowerWatts = p.PowerWatts,
            CadenceRpm = power.CadenceRpm,
            RollerRpm = p.RollerRpm,
            SpeedMetersPerSecond = speedMps,
            Coasting = p.Coasting,
            Mode = p.State switch
            {
                InRideSensorState.SpindownIdle => TrainerSensorMode.CalibrationIdle,
                InRideSensorState.SpindownReady => TrainerSensorMode.CalibrationReady,
                InRideSensorState.SpindownActive => TrainerSensorMode.CalibrationActive,
                _ => TrainerSensorMode.Normal,
            },
            Summary =
                $"P={p.PowerWatts}W cad={power.CadenceRpm:0.0} (calib {p.CadenceCalibratedRpm:0.0}, raw {p.CadenceRaw}) " +
                $"roller={p.RollerRpm:0.0}rpm speed={p.SpeedKph:0.00}kph ({UnitConversions.ToMph(speedMps):0.00}mph) " +
                $"ticks={p.Ticks}/{p.Revolutions}rev prev={p.TicksPrevious}/{p.RevolutionsPrevious} interval={p.Interval} " +
                $"coast={p.Coasting} alpha={p.Alpha:0.0} state={p.State} cmd={p.CommandResult.Describe()} " +
                $"spindown={p.LastSpindownResultSeconds:0.000}s applied={p.SpindownTimeSeconds:0.00}s pro={p.ProFlywheel} cal={p.CalibrationResult} " +
                $"rot={p.Rotation} plain={p.PlainHex}",
            DecodedHex = p.PlainHex,
            StateText = p.State.ToString(),
            CommandText = p.CommandResult.Describe(),
            Detail = $"ticks {p.Ticks}/{p.Revolutions} prev {p.TicksPrevious}/{p.RevolutionsPrevious} int {p.Interval} coast {(p.Coasting ? "Y" : "N")} " +
                     $"alpha {p.Alpha:0} sd {p.LastSpindownResultSeconds:0.000}s cal {p.CalibrationResult} rot {p.Rotation}",
            InRide = power,
        };
    }

    public StatusInterpretation InterpretConfiguration(byte[] data) => InterpretStatus(data);

    public StatusInterpretation InterpretStatus(byte[] data)
    {
        var result = InRideProtocol.ProcessConfigurationData(data);
        if (!result.IsSuccess)
        {
            return new StatusInterpretation(false, $"Configuration rejected ({result.Error}/{(int)result.Error}): {result.Message}");
        }
        LatestConfiguration = result.Value;
        var warning = InRideUpdateRateExtensions.IsDefined(result.Value.UpdateRateDefaultRaw)
            ? null
            : $"Configuration update rate 0x{result.Value.UpdateRateDefaultRaw:X4} is not one of the SDK values (0x20/0x10/0x08).";
        return new StatusInterpretation(true, $"Configuration: {result.Value}", warning);
    }

    public StatusInterpretation? InterpretControlResponse(byte[] data) => null;

    public string? DescribeLatestConfiguration() => LatestConfiguration is { } c
        ? $"Update rate: {InRideUpdateRateExtensions.Describe(c.UpdateRateDefaultRaw)}\n" +
          $"Calibration rate: {InRideUpdateRateExtensions.Describe(c.UpdateRateCalibrationRaw)}\n" +
          $"Current spindown: {c.CurrentSpindownSeconds:0.000} s ({c.CurrentSpindownTicks} ticks){(c.ProFlywheel ? " - pro flywheel" : "")}\n" +
          $"Calibration ticks: ready {c.CalibrationReadyTicks}, start {c.CalibrationStartTicks}, end {c.CalibrationEndTicks}, debounce {c.CalibrationDebounceTicks}\n" +
          $"Raw: {c.RawHex}"
        : null;

    public void Reset()
    {
        lock (_gate)
        {
            _protocol.ResetCadenceSmoothing();
        }
        LatestConfiguration = null;
    }
}

/// <summary>Bluetooth SIG Fitness Machine Service (Indoor Bike Data) - what the Kinetic "inRide" FW 1.2 unit speaks.</summary>
public sealed class FtmsInterpreter : ITrainerInterpreter
{
    private SpinDownStatus? _spinDown;
    private string? _lastStatusText;

    public TrainerProtocolKind Kind => TrainerProtocolKind.FitnessMachine;

    public string Name => "Bluetooth Fitness Machine (FTMS 0x1826)";

    /// <summary>Unused: FTMS reports bike speed directly. Kept so the setting still round-trips.</summary>
    public double RollerCircumferenceMeters { get; set; } = Core.Settings.AppSettings.KineticRollerCircumferenceMeters;

    public FitnessMachineFeature? LatestFeature { get; private set; }

    public SpinDownStatus? LatestSpinDownStatus => _spinDown;

    public InterpretedPacket InterpretData(byte[] data, DateTimeOffset at)
    {
        var result = IndoorBikeDataParser.Parse(data);
        if (!result.IsSuccess)
        {
            return new InterpretedPacket
            {
                Accepted = false,
                RawBytes = data,
                RejectReason = result.Error,
                Summary = $"rejected: {result.Error}",
            };
        }

        var b = result.Value;
        var speedMps = b.InstantaneousSpeedKph is { } kph ? UnitConversions.KphToMetersPerSecond(kph) : (double?)null;
        var mode = _spinDown switch
        {
            SpinDownStatus.SpinDownRequested => TrainerSensorMode.CalibrationIdle,
            SpinDownStatus.StopPedaling => TrainerSensorMode.CalibrationActive,
            _ => TrainerSensorMode.Normal,
        };
        return new InterpretedPacket
        {
            Accepted = true,
            RawBytes = data,
            PowerWatts = b.InstantaneousPowerWatts,
            CadenceRpm = b.InstantaneousCadenceRpm,
            RollerRpm = null,
            SpeedMetersPerSecond = speedMps,
            Coasting = null,
            Mode = mode,
            Summary = $"{b} ({(speedMps is { } s ? UnitConversions.ToMph(s).ToString("0.00") + "mph" : "no speed")})",
            DecodedHex = null,
            StateText = _spinDown?.ToString() ?? "Normal",
            CommandText = "",
            Detail = $"flags 0x{(ushort)b.Flags:X4}" +
                     (b.TotalDistanceMeters is { } d ? $" dist {d}m" : "") +
                     (b.ElapsedTimeSeconds is { } e ? $" elapsed {e}s" : "") +
                     (b.AveragePowerWatts is { } ap ? $" avgP {ap}W" : "") +
                     (b.HeartRateBpm is { } hr ? $" hr {hr}" : ""),
            Ftms = b,
        };
    }

    public StatusInterpretation InterpretConfiguration(byte[] data)
    {
        var result = FitnessMachineFeature.Parse(data);
        if (!result.IsSuccess)
        {
            return new StatusInterpretation(false, $"Fitness Machine Feature rejected: {result.Error}");
        }
        LatestFeature = result.Value;
        var f = result.Value;
        var warning = f.SupportsPower ? null : "This machine does not declare power measurement; the power field may never appear.";
        return new StatusInterpretation(true, $"Fitness Machine Feature: {f}", warning);
    }

    public StatusInterpretation InterpretStatus(byte[] data)
    {
        var result = FitnessMachineStatusEvent.Parse(data);
        if (!result.IsSuccess)
        {
            return new StatusInterpretation(false, $"Fitness Machine Status rejected: {result.Error}");
        }
        var evt = result.Value;
        if (evt.SpinDown is { } sd)
        {
            _spinDown = sd is SpinDownStatus.Success or SpinDownStatus.Error ? null : sd;
        }
        _lastStatusText = $"Machine status: {evt}";
        return new StatusInterpretation(true, _lastStatusText, Payload: evt);
    }

    public StatusInterpretation? InterpretControlResponse(byte[] data)
    {
        var result = FtmsControlPoint.ParseResponse(data);
        return result.IsSuccess
            ? new StatusInterpretation(true, $"Control point response: {result.Value}", Payload: result.Value)
            : new StatusInterpretation(false, $"Control point response rejected: {result.Error}");
    }

    public string? DescribeLatestConfiguration()
    {
        if (LatestFeature is not { } f)
        {
            return null;
        }
        var text =
            $"Measures: {(f.SupportsPower ? "power, " : "")}{(f.SupportsCadence ? "cadence, " : "")}speed\n" +
            $"Spin-down calibration: {(f.SupportsSpinDown ? "supported" : "not supported")}\n" +
            $"Resistance control: {(f.SupportsTargetPower || f.SupportsTargetResistance ? "yes" : "no (read-only trainer)")}\n" +
            $"Features: 0x{(uint)f.Features:X8} [{f.Features}]\nTargets: 0x{(uint)f.TargetSettings:X8} [{f.TargetSettings}]\n" +
            $"Raw: {f.RawHex}";
        return _lastStatusText is null ? text : text + "\n" + _lastStatusText;
    }

    public void Reset()
    {
        LatestFeature = null;
        _spinDown = null;
        _lastStatusText = null;
    }
}
