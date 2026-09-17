using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Core.Timing;
using KineticTrainer.Protocol.InRide;

namespace KineticTrainer.Bluetooth.Pipeline;

/// <summary>
/// Raw BLE bytes -> protocol interpreter -> <see cref="TrainerTelemetry"/>. The only place sensor bytes are interpreted.
/// Every packet, accepted or rejected, is written to the diagnostic log with its hex payload; logging never throws.
/// </summary>
public sealed class TrainerTelemetryPipeline
{
    private readonly ITelemetryPublisher _publisher;
    private readonly IDiagnosticLog _log;
    private readonly RateEstimator _rate = new(TimeSpan.FromSeconds(10));
    private readonly object _gate = new();
    private ITrainerInterpreter _interpreter;
    private long _accepted;
    private long _rejected;
    private double _rollerCircumference = Core.Settings.AppSettings.KineticRollerCircumferenceMeters;
    private bool _ignoreSystemId;

    public TrainerTelemetryPipeline(ITelemetryPublisher publisher, IDiagnosticLog log)
    {
        _publisher = publisher;
        _log = log;
        _interpreter = new InRideInterpreter();
    }

    public ITrainerInterpreter Interpreter => _interpreter;

    public TrainerProtocolKind Protocol => _interpreter.Kind;

    /// <summary>Metres per roller revolution; from settings. Only the inRide protocol derives speed from it.</summary>
    public double RollerCircumferenceMeters
    {
        get => _rollerCircumference;
        set
        {
            _rollerCircumference = value;
            _interpreter.RollerCircumferenceMeters = value;
        }
    }

    public bool IgnoreSystemIdValidation
    {
        get => _ignoreSystemId;
        set
        {
            _ignoreSystemId = value;
            if (_interpreter is InRideInterpreter ir)
            {
                ir.IgnoreSystemIdValidation = value;
            }
        }
    }

    public byte[]? SystemId { get; private set; }

    public string? SensorName { get; private set; }

    public bool IsSimulated { get; private set; }

    public InterpretedPacket? LatestPacket { get; private set; }

    /// <summary>Text of the most recent configuration/feature read, for the Sensor page.</summary>
    public string? LatestConfigurationText { get; private set; }

    public DateTimeOffset? LastPacketAt { get; private set; }

    public long AcceptedPackets => Interlocked.Read(ref _accepted);

    public long RejectedPackets => Interlocked.Read(ref _rejected);

    public double? PacketRateHz => _rate.RateHz(DateTimeOffset.UtcNow);

    public TimeSpan? PacketMedianInterval => _rate.MedianInterval(DateTimeOffset.UtcNow);

    /// <summary>Raised for every accepted data packet (after publishing telemetry). For the debug view.</summary>
    public event Action<InterpretedPacket, TrainerTelemetry>? PacketAccepted;

    /// <summary>Raised when a configuration/feature read or status notification was interpreted (multi-line text).</summary>
    public event Action<string>? ConfigurationUpdated;

    /// <summary>Raised for FTMS control point responses and inRide command results.</summary>
    public event Action<string>? ControlResponse;

    /// <summary>Typed FTMS Fitness Machine Status events (spin-down progress etc.).</summary>
    public event Action<Protocol.Ftms.FitnessMachineStatusEvent>? FtmsStatusReceived;

    /// <summary>Typed FTMS control point responses.</summary>
    public event Action<Protocol.Ftms.FtmsControlPointResponse>? FtmsControlResponseReceived;

    /// <summary>Call when a connection is established so the interpreter matches the sensor and no state carries over.</summary>
    public void BeginConnection(ISensorLink link, bool simulated)
    {
        var interpreter = link.Protocol switch
        {
            TrainerProtocolKind.FitnessMachine => new FtmsInterpreter(),
            _ => (ITrainerInterpreter)new InRideInterpreter { SystemId = link.DeviceInfo.SystemId, IgnoreSystemIdValidation = _ignoreSystemId },
        };
        UseInterpreter(interpreter, link.DeviceInfo.SystemId, link.DeviceInfo.Name, simulated);
        _log.Info(DiagnosticCategory.Connection, $"Protocol: {_interpreter.Name}");
    }

    /// <summary>Installs an interpreter directly (tests, or diagnostics without a link).</summary>
    public void UseInterpreter(ITrainerInterpreter interpreter, byte[]? systemId = null, string? sensorName = null, bool simulated = false)
    {
        lock (_gate)
        {
            _interpreter = interpreter;
            _interpreter.RollerCircumferenceMeters = _rollerCircumference;
            SystemId = systemId ?? (interpreter as InRideInterpreter)?.SystemId;
            SensorName = sensorName;
            IsSimulated = simulated;
            _rate.Clear();
            LatestPacket = null;
            LatestConfigurationText = null;
            LastPacketAt = null;
            Interlocked.Exchange(ref _accepted, 0);
            Interlocked.Exchange(ref _rejected, 0);
        }
    }

    public void OnDataNotification(byte[] data, DateTimeOffset receivedAt, TrainerConnectionState connectionState)
    {
        try
        {
            InterpretedPacket packet;
            lock (_gate)
            {
                packet = _interpreter.InterpretData(data, receivedAt);
            }

            if (!packet.Accepted)
            {
                Interlocked.Increment(ref _rejected);
                _log.Warn(DiagnosticCategory.PacketRejected, $"Data packet rejected: {packet.RejectReason}", packet.RawHex);
                return;
            }

            _rate.Mark(receivedAt);
            Interlocked.Increment(ref _accepted);
            LatestPacket = packet;
            LastPacketAt = receivedAt;

            var telemetry = new TrainerTelemetry
            {
                Timestamp = receivedAt,
                ConnectionState = connectionState,
                IsDataValid = true,
                PowerWatts = packet.PowerWatts,
                CadenceRpm = packet.CadenceRpm,
                RollerRpm = packet.RollerRpm,
                SpeedMetersPerSecond = packet.SpeedMetersPerSecond,
                Coasting = packet.Coasting,
                SensorMode = packet.Mode,
                SensorId = SystemId is null ? null : Convert.ToHexStringLower(SystemId),
                SensorName = SensorName,
                IsSimulated = IsSimulated,
            };
            _publisher.Publish(telemetry);

            _log.Debug(DiagnosticCategory.PowerPacket, packet.Summary, packet.RawHex);

            if (packet.InRide is { } ir && ir.Packet.CommandResult != InRideCommandResult.None)
            {
                var text = $"Sensor reported command result: {ir.Packet.CommandResult}";
                _log.Info(DiagnosticCategory.Command, text);
                ControlResponse?.Invoke(text);
            }

            PacketAccepted?.Invoke(packet, telemetry);
        }
        catch (Exception ex)
        {
            // A bug in parsing or a consumer must never take down the BLE callback thread.
            Interlocked.Increment(ref _rejected);
            _log.Error(DiagnosticCategory.PacketRejected, $"Unexpected error processing data packet: {ex}", Convert.ToHexStringLower(data));
        }
    }

    /// <summary>Configuration (inRide) or Fitness Machine Feature (FTMS) read after connecting or on demand.</summary>
    public string? OnConfigurationRead(byte[] data)
    {
        try
        {
            StatusInterpretation result;
            lock (_gate)
            {
                result = _interpreter.InterpretConfiguration(data);
            }
            return Record(result, data, "read");
        }
        catch (Exception ex)
        {
            _log.Error(DiagnosticCategory.PacketRejected, $"Unexpected error processing configuration: {ex}", Convert.ToHexStringLower(data));
            return null;
        }
    }

    public void OnStatusNotification(byte[] data)
    {
        try
        {
            StatusInterpretation result;
            lock (_gate)
            {
                result = _interpreter.InterpretStatus(data);
            }
            Record(result, data, "notify");
            if (result.Payload is Protocol.Ftms.FitnessMachineStatusEvent evt)
            {
                FtmsStatusReceived?.Invoke(evt);
            }
        }
        catch (Exception ex)
        {
            _log.Error(DiagnosticCategory.PacketRejected, $"Unexpected error processing status notification: {ex}", Convert.ToHexStringLower(data));
        }
    }

    public void OnControlResponse(byte[] data)
    {
        try
        {
            StatusInterpretation? result;
            lock (_gate)
            {
                result = _interpreter.InterpretControlResponse(data);
            }
            if (result is null)
            {
                return;
            }
            if (result.Accepted)
            {
                _log.Info(DiagnosticCategory.Command, result.Text, Convert.ToHexStringLower(data));
            }
            else
            {
                _log.Warn(DiagnosticCategory.PacketRejected, result.Text, Convert.ToHexStringLower(data));
            }
            ControlResponse?.Invoke(result.Text);
            if (result.Payload is Protocol.Ftms.FtmsControlPointResponse response)
            {
                FtmsControlResponseReceived?.Invoke(response);
            }
        }
        catch (Exception ex)
        {
            _log.Error(DiagnosticCategory.PacketRejected, $"Unexpected error processing control response: {ex}", Convert.ToHexStringLower(data));
        }
    }

    private string? Record(StatusInterpretation result, byte[] data, string origin)
    {
        var hex = Convert.ToHexStringLower(data);
        if (!result.Accepted)
        {
            _log.Warn(DiagnosticCategory.PacketRejected, $"{result.Text} ({origin})", hex);
            return null;
        }
        _log.Info(DiagnosticCategory.ConfigPacket, $"{result.Text} ({origin})", hex);
        if (result.Warning is not null)
        {
            _log.Warn(DiagnosticCategory.PacketWarning, result.Warning);
        }
        string display;
        lock (_gate)
        {
            display = _interpreter.DescribeLatestConfiguration() ?? result.Text;
        }
        LatestConfigurationText = display;
        ConfigurationUpdated?.Invoke(display);
        return display;
    }
}
