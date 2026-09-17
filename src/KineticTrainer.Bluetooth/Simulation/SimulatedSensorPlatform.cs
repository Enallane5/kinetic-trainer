using System.Buffers.Binary;
using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Settings;
using KineticTrainer.Protocol.Ftms;
using KineticTrainer.Protocol.InRide;

namespace KineticTrainer.Bluetooth.Simulation;

/// <summary>
/// A fake trainer sensor so the whole pipeline, UI, reconnect logic and TCP bridge can be exercised with no hardware.
/// It can impersonate either protocol:
///  - FTMS (default, matches the real Kinetic "inRide" FW 1.2): Indoor Bike Data at 2 Hz, Feature 0240000000800000
///    (the value the real unit reports), control point with Request Control / Spin Down, and the spin-down status sequence.
///  - inRide 2 custom: properly obfuscated 20-byte power packets, configuration, update-rate command with the result
///    echoed in the next packet, and Kinetic's spindown state machine.
/// Clearly marked as simulated everywhere it appears.
/// </summary>
public sealed class SimulatedSensorPlatform : ISensorPlatform
{
    public const ulong SimulatedAddress = 0x51_4D_00_00_00_01; // "SIM..." reserved, never a real MAC

    private readonly IDiagnosticLog _log;

    public SimulatedSensorPlatform(IDiagnosticLog log)
    {
        _log = log;
        Connector = new SimulatedConnector(this);
    }

    public string Name => "Simulated sensor";

    public ISensorConnector Connector { get; }

    public SimulatedSensorLink? ActiveLink { get; private set; }

    /// <summary>Behaviour knobs shared by every simulated link (settable from the debug view).</summary>
    public SimulatedRideProfile Profile { get; } = new();

    public bool Owns(ulong address) => address == SimulatedAddress;

    public ISensorScanner CreateScanner() => new SimulatedScanner(Profile);

    private sealed class SimulatedScanner : ISensorScanner
    {
        private readonly SimulatedRideProfile _profile;
        private CancellationTokenSource? _cts;

        public SimulatedScanner(SimulatedRideProfile profile)
        {
            _profile = profile;
        }

        public event Action<DiscoveredSensor>? SensorSeen;

        public bool IsScanning => _cts is not null;

        public Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var ftms = _profile.Protocol == TrainerProtocolKind.FitnessMachine;
                    SensorSeen?.Invoke(new DiscoveredSensor(SimulatedAddress, "inRide SIM", -50, !ftms, DateTimeOffset.UtcNow, IsSimulated: true,
                        AddressType: SensorAddressType.Random, AdvertisesFitnessMachineService: ftms));
                    try
                    {
                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }, token);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new(StopAsync());
    }

    private sealed class SimulatedConnector : ISensorConnector
    {
        private readonly SimulatedSensorPlatform _platform;

        public SimulatedConnector(SimulatedSensorPlatform platform)
        {
            _platform = platform;
        }

        public async Task<ISensorLink> ConnectAsync(ulong address, SensorAddressType addressType, CancellationToken ct)
        {
            if (address != SimulatedAddress)
            {
                throw new SensorConnectException("Simulator only owns the simulated address.");
            }
            if (_platform.Profile.Unreachable)
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
                throw new SensorConnectException("Simulated sensor is switched off.", deviceUnreachable: true);
            }
            await Task.Delay(400, ct).ConfigureAwait(false);
            var link = new SimulatedSensorLink(_platform.Profile, _platform._log);
            _platform.ActiveLink = link;
            link.Start();
            return link;
        }
    }
}

/// <summary>Adjustable behaviour of the simulated sensor.</summary>
public sealed class SimulatedRideProfile
{
    public volatile bool Unreachable;

    /// <summary>Which protocol the fake sensor speaks. Fixed for the lifetime of a connection.</summary>
    public TrainerProtocolKind Protocol { get; set; } = TrainerProtocolKind.FitnessMachine;

    /// <summary>Target roller speed in km/h (the "rider").</summary>
    public double TargetSpeedKph { get; set; } = 32;

    public double CadenceRpm { get; set; } = 88;

    public double SpindownSeconds { get; set; } = 1.78;

    public InRideUpdateRate UpdateRate { get; set; } = InRideUpdateRate.Millis1000;

    /// <summary>Emit an occasional malformed packet (wrong length) to exercise rejection paths.</summary>
    public bool EmitMalformedPackets { get; set; }

    public byte[] SystemId { get; set; } = [0xC0, 0xFF, 0xEE, 0x12, 0x34, 0x56];
}

public sealed class SimulatedSensorLink : ISensorLink
{
    private readonly SimulatedRideProfile _profile;
    private readonly IDiagnosticLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _rng = new();
    private Task? _loop;
    private double _speedKph;
    private uint _prevTicks;
    private byte _prevRevs;
    private InRideSensorState _state = InRideSensorState.Normal;
    private InRideCommandResult _pendingResult = InRideCommandResult.None;
    private InRideUpdateRate _rate;
    private int _packetsSinceStateChange;
    private bool _disposed;
    private bool _ftmsControlGranted;
    private SpinDownStatus? _ftmsSpinDown;
    private uint _ftmsDistance;
    private double _ftmsDistanceFraction;

    internal SimulatedSensorLink(SimulatedRideProfile profile, IDiagnosticLog log)
    {
        _profile = profile;
        _log = log;
        _rate = profile.UpdateRate;
        Protocol = profile.Protocol;
        DeviceInfo = Protocol == TrainerProtocolKind.FitnessMachine
            ? new SensorDeviceInfo("inRide SIM", null, "1.2", "10", "Kinetic (simulated)", "inRide", 100)
            : new SensorDeviceInfo("inRide SIM", profile.SystemId, "sim-1.0", "sim", "Kinetic (simulated)");
    }

    public ulong Address => SimulatedSensorPlatform.SimulatedAddress;

    public SensorDeviceInfo DeviceInfo { get; }

    public bool IsConnected => !_disposed && !_cts.IsCancellationRequested;

    public TrainerProtocolKind Protocol { get; }

    public SensorAddressType AddressType => SensorAddressType.Random;

    public bool StatusSupportsNotify => true;

    public event Action<byte[], DateTimeOffset>? DataNotified;

    public event Action<byte[], DateTimeOffset>? StatusNotified;

    public event Action<byte[], DateTimeOffset>? ControlResponded;

    public event Action<string>? Disconnected;

    internal void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Simulates the sensor going out of range: the link drops and reports it.</summary>
    public void SimulateDrop(string reason = "simulated signal loss")
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }
        _cts.Cancel();
        Disconnected?.Invoke(reason);
    }

    public Task<byte[]> ReadConfigurationAsync(CancellationToken ct)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Simulated link is closed.");
        }
        return Task.FromResult(Protocol == TrainerProtocolKind.FitnessMachine ? BuildFtmsFeature() : BuildInRideConfiguration());
    }

    public Task WriteControlPointAsync(byte[] payload, CancellationToken ct)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Simulated link is closed.");
        }
        if (Protocol == TrainerProtocolKind.FitnessMachine)
        {
            HandleFtmsControl(payload);
        }
        else
        {
            HandleInRideControl(payload);
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ FTMS

    private static byte[] BuildFtmsFeature() => [0x02, 0x40, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00]; // as read from the real unit

    private void HandleFtmsControl(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return;
        }
        var op = (FtmsOpCode)payload[0];
        byte[] response;
        switch (op)
        {
            case FtmsOpCode.RequestControl:
                _ftmsControlGranted = true;
                response = [0x80, payload[0], (byte)FtmsResultCode.Success];
                break;
            case FtmsOpCode.SpinDownControl when payload.Length >= 2 && payload[1] == (byte)SpinDownControlParameter.Start:
                if (!_ftmsControlGranted)
                {
                    response = [0x80, payload[0], (byte)FtmsResultCode.ControlNotPermitted];
                    break;
                }
                // Response parameter: target speed low/high (0.01 km/h): coast from 40 down to 20 km/h.
                response = new byte[7];
                response[0] = 0x80; response[1] = payload[0]; response[2] = (byte)FtmsResultCode.Success;
                BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(3), 2000);
                BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(5), 4000);
                _ftmsSpinDown = SpinDownStatus.SpinDownRequested;
                _ = Task.Run(async () =>
                {
                    StatusNotified?.Invoke([(byte)FtmsStatusOpCode.SpinDownStatus, (byte)SpinDownStatus.SpinDownRequested], DateTimeOffset.UtcNow);
                    await Task.Delay(1500).ConfigureAwait(false);
                    _ftmsSpinDown = SpinDownStatus.StopPedaling;
                    StatusNotified?.Invoke([(byte)FtmsStatusOpCode.SpinDownStatus, (byte)SpinDownStatus.StopPedaling], DateTimeOffset.UtcNow);
                    await Task.Delay(2500).ConfigureAwait(false);
                    _ftmsSpinDown = null;
                    StatusNotified?.Invoke([(byte)FtmsStatusOpCode.SpinDownStatus, (byte)SpinDownStatus.Success], DateTimeOffset.UtcNow);
                });
                break;
            case FtmsOpCode.SpinDownControl:
                _ftmsSpinDown = null;
                response = [0x80, payload[0], (byte)FtmsResultCode.Success];
                break;
            case FtmsOpCode.Reset:
            case FtmsOpCode.StartOrResume:
            case FtmsOpCode.StopOrPause:
                response = [0x80, payload[0], _ftmsControlGranted ? (byte)FtmsResultCode.Success : (byte)FtmsResultCode.ControlNotPermitted];
                break;
            default:
                response = [0x80, payload[0], (byte)FtmsResultCode.OpCodeNotSupported];
                break;
        }
        _log.Info(DiagnosticCategory.Command, $"[SIM] FTMS control {op} -> {(FtmsResultCode)response[2]}");
        _ = Task.Run(async () =>
        {
            await Task.Delay(50).ConfigureAwait(false);
            ControlResponded?.Invoke(response, DateTimeOffset.UtcNow);
        });
    }

    private byte[] BuildFtmsPacket()
    {
        var coasting = _ftmsSpinDown is not null || _profile.TargetSpeedKph <= 0;
        var cadence = coasting ? 0 : Math.Max(0, _profile.CadenceRpm + (_rng.NextDouble() - 0.5) * 4);
        // Same shape as Kinetic's power model in the inRide 2 SDK, so the numbers look familiar.
        var power = coasting ? (short)0 : (short)Math.Max(0, InRidePowerParser.PowerForSpeed(_speedKph, _profile.SpindownSeconds));
        _ftmsDistanceFraction += _speedKph / 3.6 * 0.5;
        var whole = (uint)_ftmsDistanceFraction;
        _ftmsDistance += whole;
        _ftmsDistanceFraction -= whole;
        return IndoorBikeDataParser.Build(speedKph: _speedKph, cadenceRpm: cadence, powerWatts: power);
    }

    // ------------------------------------------------------------------ inRide 2 custom

    private void HandleInRideControl(byte[] payload)
    {
        if (payload.Length < 3)
        {
            _pendingResult = InRideCommandResult.InvalidRequest;
            return;
        }
        var key = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
        if (key != InRideCommands.CommandKey(_profile.SystemId))
        {
            _pendingResult = InRideCommandResult.InvalidRequest;
            _log.Warn(DiagnosticCategory.Command, $"[SIM] command key 0x{key:X4} does not match this sensor");
            return;
        }
        switch ((InRideCommand)payload[2])
        {
            case InRideCommand.SetSpindownParams when payload.Length == 15:
                var raw = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(11, 2));
                if (InRideUpdateRateExtensions.IsDefined(raw))
                {
                    _rate = (InRideUpdateRate)raw;
                    _pendingResult = InRideCommandResult.Success;
                    _log.Info(DiagnosticCategory.Command, $"[SIM] update rate set to {_rate.Hz()} Hz");
                    StatusNotified?.Invoke(BuildInRideConfiguration(), DateTimeOffset.UtcNow);
                }
                else
                {
                    _pendingResult = InRideCommandResult.InvalidRequest;
                }
                break;
            case InRideCommand.StartCalibration:
                _state = InRideSensorState.SpindownIdle;
                _packetsSinceStateChange = 0;
                _pendingResult = InRideCommandResult.Success;
                break;
            case InRideCommand.StopCalibration:
                _state = InRideSensorState.Normal;
                _pendingResult = InRideCommandResult.Success;
                break;
            case InRideCommand.SetSpindownTime when payload.Length == 7:
                _profile.SpindownSeconds = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(3, 4)) / InRideConstants.SensorHz;
                _pendingResult = InRideCommandResult.Success;
                break;
            case InRideCommand.SetName:
                _pendingResult = InRideCommandResult.NotSupported;
                break;
            default:
                _pendingResult = InRideCommandResult.InvalidRequest;
                break;
        }
    }

    private byte[] BuildInRideConfiguration()
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), InRideConstants.CalibrationReadyTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), InRideConstants.CalibrationStartTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), InRideConstants.CalibrationEndTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), InRideConstants.CalibrationDebounceTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)(_profile.SpindownSeconds * InRideConstants.SensorHz));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)_rate);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), (ushort)InRideUpdateRate.Millis250);
        return b;
    }

    private byte[] BuildInRidePacket(InRideUpdateRate rate)
    {
        var interval = (uint)((ushort)rate * (InRideConstants.SensorHz / 32.0));
        var revsPerSecond = _speedKph / 3.6 / (InRideConstants.RollerCircumferenceKm * 1000);
        var revs = (byte)Math.Clamp(Math.Round(revsPerSecond / rate.Hz()), 0, 255);
        var ticks = revs == 0 ? 0u : (uint)Math.Round(revs / revsPerSecond * InRideConstants.SensorHz);

        var coasting = _state != InRideSensorState.Normal || _profile.TargetSpeedKph <= 0;
        var cadence = coasting ? 0 : _profile.CadenceRpm + (_rng.NextDouble() - 0.5) * 4;
        var cadenceRaw = cadence <= 0 ? (ushort)0 : (ushort)Math.Max(1, Math.Round((cadence - InRideConstants.CadenceIntercept) / InRideConstants.CadenceSlope));

        var plain = new byte[20];
        plain[0] = (byte)((_rng.Next(4) << 6) | (byte)_state | (byte)_pendingResult);
        _pendingResult = InRideCommandResult.None;
        plain[1] = (byte)interval; plain[2] = (byte)(interval >> 8); plain[3] = (byte)(interval >> 16);
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4), ticks);
        plain[8] = revs;
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(9), _prevTicks);
        plain[13] = _prevRevs;
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(14), cadenceRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(16), (uint)(_profile.SpindownSeconds * InRideConstants.SensorHz));
        _prevTicks = ticks;
        _prevRevs = revs;
        return InRideDeobfuscator.Obfuscate(plain);
    }

    // ------------------------------------------------------------------ shared rider model

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[] packet;
                if (Protocol == TrainerProtocolKind.FitnessMachine)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false); // the real unit notifies at ~2 Hz
                    Step(0.5);
                    packet = BuildFtmsPacket();
                }
                else
                {
                    var rate = _state == InRideSensorState.Normal ? _rate : InRideUpdateRate.Millis250;
                    await Task.Delay(rate.Interval(), ct).ConfigureAwait(false);
                    Step(1.0 / rate.Hz());
                    packet = BuildInRidePacket(rate);
                }
                if (_profile.EmitMalformedPackets && _rng.Next(10) == 0)
                {
                    packet = packet.Take(_rng.Next(1, Math.Max(2, packet.Length - 1))).ToArray();
                }
                DataNotified?.Invoke(packet, DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Step(double dt)
    {
        var target = _profile.TargetSpeedKph;
        if (Protocol == TrainerProtocolKind.FitnessMachine)
        {
            target = _ftmsSpinDown switch
            {
                SpinDownStatus.SpinDownRequested => 42, // rider accelerates above the spin-down window
                SpinDownStatus.StopPedaling => 0,       // coasting down
                _ => target,
            };
        }
        else
        {
            switch (_state)
            {
                case InRideSensorState.SpindownIdle:
                    target = 36; // rider accelerates past ~33 km/h (calibrationReadySpeedKPH)
                    if (_speedKph > InRideProtocol.CalibrationReadySpeedKph && ++_packetsSinceStateChange > 8)
                    {
                        _state = InRideSensorState.SpindownReady;
                        _packetsSinceStateChange = 0;
                    }
                    break;
                case InRideSensorState.SpindownReady:
                    target = 0; // rider coasts
                    if (_speedKph < InRideProtocol.CalibrationStartSpeedKph)
                    {
                        _state = InRideSensorState.SpindownActive;
                        _packetsSinceStateChange = 0;
                    }
                    break;
                case InRideSensorState.SpindownActive:
                    target = 0;
                    if (_speedKph < InRideProtocol.CalibrationEndSpeedKph)
                    {
                        _state = InRideSensorState.Normal;
                        _pendingResult = InRideCommandResult.CalibrationResult;
                        _profile.SpindownSeconds = 1.5 + _rng.NextDouble() * 0.5;
                        StatusNotified?.Invoke(BuildInRideConfiguration(), DateTimeOffset.UtcNow);
                    }
                    break;
            }
        }
        // First-order approach to the target with a little noise; coasting decelerates faster than accelerating.
        var k = target < _speedKph ? 0.9 : 0.5;
        _speedKph += (target - _speedKph) * Math.Min(1, k * dt);
        _speedKph = Math.Max(0, _speedKph + (_rng.NextDouble() - 0.5) * 0.4);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
        _cts.Dispose();
    }
}
