using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Bluetooth.Calibration;
using KineticTrainer.Bluetooth.Pipeline;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Settings;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Core.Timing;
using KineticTrainer.Protocol.Ftms;
using KineticTrainer.Protocol.InRide;

namespace KineticTrainer.Bluetooth.Session;

/// <summary>Options controlling reconnection and stale-data handling.</summary>
public sealed record SensorSessionOptions
{
    public TimeSpan ReconnectInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>With no packet for this long the last values are considered stale and a no-data sample is published.</summary>
    public TimeSpan StaleDataTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>With no packet for this long the link is assumed dead even if the OS still says connected.</summary>
    public TimeSpan DeadLinkTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Owns the connection to one trainer sensor for the lifetime of the app: connect on request or automatically to
/// the remembered sensor, keep the link alive, reconnect with backoff when it drops, and feed every packet through
/// the <see cref="TrainerTelemetryPipeline"/>. Nothing here touches the UI; state is exposed through events and the
/// telemetry stream.
/// </summary>
public sealed class SensorSession : IAsyncDisposable
{
    private readonly IReadOnlyList<ISensorPlatform> _platforms;
    private readonly TrainerTelemetryPipeline _pipeline;
    private readonly ITelemetryPublisher _publisher;
    private readonly IDiagnosticLog _log;
    private readonly SensorSessionOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private ISensorLink? _link;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private Task? _watchdogTask;
    private TrainerConnectionState _state = TrainerConnectionState.Disconnected;
    private bool _userDisconnected;
    private ulong _targetAddress;
    private SensorAddressType _targetAddressType;
    private string? _targetName;
    private bool _staleReported;

    public SensorSession(IReadOnlyList<ISensorPlatform> platforms, TrainerTelemetryPipeline pipeline, ITelemetryPublisher publisher,
        IDiagnosticLog log, SensorSessionOptions? options = null)
    {
        _platforms = platforms;
        _pipeline = pipeline;
        _publisher = publisher;
        _log = log;
        _options = options ?? new SensorSessionOptions();
        _pipeline.FtmsControlResponseReceived += r => Calibration?.OnControlResponse(r, DateTimeOffset.UtcNow);
        _pipeline.FtmsStatusReceived += s => Calibration?.OnStatus(s, DateTimeOffset.UtcNow);
        _pipeline.PacketAccepted += (_, t) => Calibration?.OnSpeed(t.SpeedMetersPerSecond, DateTimeOffset.UtcNow);
        _watchdogTask = Task.Run(() => WatchdogAsync(_disposeCts.Token));
    }

    /// <summary>The spin-down in progress (or the last one), for the UI.</summary>
    public SpinDownCalibration? Calibration { get; private set; }

    public event Action<CalibrationSnapshot>? CalibrationChanged;

    /// <summary>Starts an FTMS spin-down. The state machine drives the control-point writes; progress arrives via <see cref="CalibrationChanged"/>.</summary>
    public SpinDownCalibration StartCalibration(Func<double, string>? formatSpeed = null)
    {
        var link = RequireFtms();
        if (Calibration is { IsActive: true } running)
        {
            return running;
        }
        var calibration = new SpinDownCalibration();
        if (formatSpeed is not null)
        {
            calibration.FormatSpeed = formatSpeed;
        }
        calibration.Changed += snapshot =>
        {
            if (!snapshot.IsActive)
            {
                _log.Info(DiagnosticCategory.Command, $"Calibration ended: {snapshot.Phase} - {snapshot.Result}");
            }
            CalibrationChanged?.Invoke(snapshot);
        };
        calibration.CommandRequested += bytes => _ = Task.Run(async () =>
        {
            try
            {
                _log.Info(DiagnosticCategory.Command, "Calibration: writing control point", Convert.ToHexStringLower(bytes));
                await link.WriteControlPointAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                calibration.Fail($"Could not write to the sensor: {ex.Message}", DateTimeOffset.UtcNow);
            }
        });
        Calibration = calibration;
        _log.Info(DiagnosticCategory.Command, "Calibration started (FTMS spin-down)");
        calibration.Begin(DateTimeOffset.UtcNow);
        _ = Task.Run(async () =>
        {
            while (calibration.IsActive && !_disposeCts.IsCancellationRequested)
            {
                await Task.Delay(500).ConfigureAwait(false);
                calibration.Tick(DateTimeOffset.UtcNow);
            }
        });
        return calibration;
    }

    public void CancelCalibration() => Calibration?.Cancel(DateTimeOffset.UtcNow);

    public TrainerConnectionState State => _state;

    public bool AutoReconnect { get; set; } = true;

    /// <summary>Applied on connect for inRide custom sensors; FTMS sensors have no update-rate command.</summary>
    public SensorUpdateRate? PreferredUpdateRate { get; set; }

    public TrainerTelemetryPipeline Pipeline => _pipeline;

    public SensorDeviceInfo? DeviceInfo => _link?.DeviceInfo;

    public TrainerProtocolKind? Protocol => _link?.Protocol;

    public ulong? ConnectedAddress => _link?.Address;

    public ulong? TargetAddress => _targetAddress == 0 ? null : _targetAddress;

    public string? TargetName => _targetName;

    public event Action<TrainerConnectionState>? StateChanged;

    /// <summary>Raised after a successful connect with everything needed to remember the sensor.</summary>
    public event Action<SavedSensor>? SensorConnected;

    /// <summary>User-initiated connection to a specific sensor. Cancels any reconnect loop first.</summary>
    public async Task ConnectAsync(ulong address, SensorAddressType addressType, string? name, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopReconnectLoopAsync().ConfigureAwait(false);
            await TearDownLinkAsync("switching sensor").ConfigureAwait(false);
            _userDisconnected = false;
            _targetAddress = address;
            _targetAddressType = addressType;
            _targetName = name;
            SetState(TrainerConnectionState.Connecting);
            try
            {
                await EstablishAsync(address, addressType, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error(DiagnosticCategory.Connection, $"Connect to {DiscoveredSensor.FormatAddress(address)} failed: {ex.Message}");
                SetState(TrainerConnectionState.Disconnected);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Start-up path: keep trying to reach the remembered sensor until it appears or the user intervenes.</summary>
    public async Task ReconnectToSavedAsync(SavedSensor saved)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_link is not null)
            {
                return;
            }
            await StopReconnectLoopAsync().ConfigureAwait(false);
            _userDisconnected = false;
            _targetAddress = saved.BluetoothAddress;
            _targetAddressType = saved.AddressType;
            _targetName = saved.Name;
            _log.Info(DiagnosticCategory.Reconnect, $"Looking for remembered sensor {saved.Name ?? "?"} ({DiscoveredSensor.FormatAddress(saved.BluetoothAddress)}, {saved.AddressType} address)");
            StartReconnectLoop(TrainerConnectionState.Searching);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>User-initiated disconnect; no automatic reconnection until the user connects again.</summary>
    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _userDisconnected = true;
            Calibration?.Cancel(DateTimeOffset.UtcNow);
            await StopReconnectLoopAsync().ConfigureAwait(false);
            await TearDownLinkAsync("user disconnect").ConfigureAwait(false);
            SetState(TrainerConnectionState.Disconnected);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Reads the configuration (inRide) or feature (FTMS) characteristic and returns its description.</summary>
    public async Task<string?> ReadConfigurationAsync(CancellationToken ct)
    {
        var link = _link ?? throw new InvalidOperationException("Not connected.");
        var bytes = await link.ReadConfigurationAsync(ct).ConfigureAwait(false);
        return _pipeline.OnConfigurationRead(bytes);
    }

    /// <summary>Writes the inRide configure-sensor command. The sensor acknowledges in the low nibble of the next power packet.</summary>
    public async Task SetUpdateRateAsync(SensorUpdateRate rate, CancellationToken ct)
    {
        var link = _link ?? throw new InvalidOperationException("Not connected.");
        if (link.Protocol != TrainerProtocolKind.InRideCustom)
        {
            throw new NotSupportedException("This sensor speaks the standard Fitness Machine profile, which has no update-rate command; it sends at its own fixed rate.");
        }
        var systemId = link.DeviceInfo.SystemId;
        var inRideRate = rate switch
        {
            SensorUpdateRate.OneHz => InRideUpdateRate.Millis1000,
            SensorUpdateRate.TwoHz => InRideUpdateRate.Millis500,
            SensorUpdateRate.FourHz => InRideUpdateRate.Millis250,
            _ => throw new ArgumentOutOfRangeException(nameof(rate)),
        };
        var cmd = InRideProtocol.ConfigureSensorCommandData(systemId ?? [], inRideRate);
        if (!cmd.IsSuccess)
        {
            _log.Error(DiagnosticCategory.Command, $"Cannot build configure command: {cmd.Message}");
            throw new InvalidOperationException(cmd.Message);
        }
        _log.Info(DiagnosticCategory.Command, $"Writing configure-sensor command (update rate {inRideRate} = {inRideRate.Hz()} Hz)", Convert.ToHexStringLower(cmd.Value));
        await link.WriteControlPointAsync(cmd.Value, ct).ConfigureAwait(false);
        _log.Info(DiagnosticCategory.Command, "Configure-sensor command written (write with response OK)");
    }

    /// <summary>FTMS: ask the machine for control (required before spin-down or targets).</summary>
    public async Task RequestControlAsync(CancellationToken ct)
    {
        var link = RequireFtms();
        _log.Info(DiagnosticCategory.Command, "FTMS Request Control", "00");
        await link.WriteControlPointAsync(FtmsControlPoint.RequestControl(), ct).ConfigureAwait(false);
    }

    /// <summary>FTMS: start the spin-down (calibration) procedure. Progress arrives via Fitness Machine Status.</summary>
    public async Task StartSpinDownAsync(CancellationToken ct)
    {
        var link = RequireFtms();
        _log.Info(DiagnosticCategory.Command, "FTMS Spin Down Control: Start", "1301");
        await link.WriteControlPointAsync(FtmsControlPoint.StartSpinDown(), ct).ConfigureAwait(false);
    }

    /// <summary>Raw control point write for the debug view.</summary>
    public async Task WriteControlPointAsync(byte[] payload, CancellationToken ct)
    {
        var link = _link ?? throw new InvalidOperationException("Not connected.");
        _log.Info(DiagnosticCategory.Command, "Writing raw control point payload", Convert.ToHexStringLower(payload));
        await link.WriteControlPointAsync(payload, ct).ConfigureAwait(false);
        _log.Info(DiagnosticCategory.Command, "Raw control point write OK");
    }

    private ISensorLink RequireFtms()
    {
        var link = _link ?? throw new InvalidOperationException("Not connected.");
        if (link.Protocol != TrainerProtocolKind.FitnessMachine)
        {
            throw new NotSupportedException("Only Fitness Machine (FTMS) sensors support this command.");
        }
        return link;
    }

    private ISensorPlatform PlatformFor(ulong address) =>
        _platforms.FirstOrDefault(p => p.Owns(address)) ?? throw new SensorConnectException("No Bluetooth platform can reach that address.");

    private async Task EstablishAsync(ulong address, SensorAddressType addressType, CancellationToken ct)
    {
        var platform = PlatformFor(address);
        _log.Info(DiagnosticCategory.Connection, $"Connecting to {DiscoveredSensor.FormatAddress(address)} ({addressType} address) via {platform.Name}");
        var link = await platform.Connector.ConnectAsync(address, addressType, ct).ConfigureAwait(false);

        var simulated = platform.Name.Contains("Simulat", StringComparison.OrdinalIgnoreCase);
        _pipeline.BeginConnection(link, simulated);

        link.DataNotified += OnData;
        link.StatusNotified += OnStatusNotified;
        link.ControlResponded += OnControlResponded;
        link.Disconnected += OnLinkDropped;
        _link = link;
        _targetName = link.DeviceInfo.Name ?? _targetName;
        _staleReported = false;

        var info = link.DeviceInfo;
        _log.Info(DiagnosticCategory.Connection, $"Connected ({link.Protocol}): {info.Describe()}");
        if (link.Protocol == TrainerProtocolKind.InRideCustom && (info.SystemId is null || info.SystemId.Length != InRideConstants.SystemIdLength))
        {
            _log.Warn(DiagnosticCategory.Connection,
                "System ID is missing or not 6 bytes. Kinetic's SDK rejects power packets in that case (error 201). " +
                "If packets are rejected, enable 'Ignore System ID validation' in Settings and report the length shown above.");
        }

        SetState(TrainerConnectionState.Connected);

        // Read configuration/feature once after connecting.
        try
        {
            var cfg = await link.ReadConfigurationAsync(ct).ConfigureAwait(false);
            _pipeline.OnConfigurationRead(cfg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn(DiagnosticCategory.Gatt, $"Configuration read failed: {ex.Message}");
        }

        if (PreferredUpdateRate is { } rate && link.Protocol == TrainerProtocolKind.InRideCustom)
        {
            try
            {
                await SetUpdateRateAsync(rate, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn(DiagnosticCategory.Command, $"Applying preferred update rate failed: {ex.Message}");
            }
        }

        var resolvedType = link.AddressType != SensorAddressType.Unspecified ? link.AddressType : addressType;
        SensorConnected?.Invoke(new SavedSensor(address, info.Name, info.SystemIdHex, DateTimeOffset.UtcNow, resolvedType));
    }

    private void OnData(byte[] data, DateTimeOffset at)
    {
        _staleReported = false;
        _pipeline.OnDataNotification(data, at, _state);
    }

    private void OnStatusNotified(byte[] data, DateTimeOffset at) => _pipeline.OnStatusNotification(data);

    private void OnControlResponded(byte[] data, DateTimeOffset at) => _pipeline.OnControlResponse(data);

    private void OnLinkDropped(string reason)
    {
        _log.Warn(DiagnosticCategory.Connection, $"Link dropped: {reason}");
        Calibration?.Fail("The sensor disconnected during calibration.", DateTimeOffset.UtcNow);
        _ = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_link is null)
                {
                    return;
                }
                await TearDownLinkAsync(reason).ConfigureAwait(false);
                if (_userDisconnected || !AutoReconnect || _disposeCts.IsCancellationRequested)
                {
                    SetState(TrainerConnectionState.Disconnected);
                    return;
                }
                StartReconnectLoop(TrainerConnectionState.Reconnecting);
            }
            finally
            {
                _lifecycle.Release();
            }
        });
    }

    private void StartReconnectLoop(TrainerConnectionState initialState)
    {
        _reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var ct = _reconnectCts.Token;
        SetState(initialState);
        _reconnectTask = Task.Run(() => ReconnectLoopAsync(_targetAddress, ct), ct);
    }

    private async Task ReconnectLoopAsync(ulong address, CancellationToken ct)
    {
        var backoff = new ExponentialBackoff(_options.ReconnectInitialDelay, _options.ReconnectMaxDelay);
        ISensorScanner? scanner = null;
        var seen = new SemaphoreSlim(0);
        try
        {
            // Advertisement sightings wake the loop early so reconnection is quick when the sensor comes back,
            // while the backoff timer bounds how often we poke the BLE stack when it is not around.
            try
            {
                scanner = PlatformFor(address).CreateScanner();
                scanner.SensorSeen += s =>
                {
                    if (s.Address != address)
                    {
                        return;
                    }
                    if (s.AddressType != SensorAddressType.Unspecified)
                    {
                        _targetAddressType = s.AddressType; // learn/refresh the address type from the live advertisement
                    }
                    if (seen.CurrentCount == 0)
                    {
                        seen.Release();
                    }
                };
                await scanner.StartAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn(DiagnosticCategory.Reconnect, $"Advertisement watcher unavailable during reconnect: {ex.Message}");
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (_link is not null || _userDisconnected)
                        {
                            return;
                        }
                        // Scanning and connecting share the radio: an active scan makes every GATT step take seconds.
                        await PauseScannerAsync(scanner).ConfigureAwait(false);
                        _log.Info(DiagnosticCategory.Reconnect, $"Reconnect attempt {backoff.Attempt + 1} to {DiscoveredSensor.FormatAddress(address)}");
                        await EstablishAsync(address, _targetAddressType, ct).ConfigureAwait(false);
                        _log.Info(DiagnosticCategory.Reconnect, "Reconnected");
                        return;
                    }
                    finally
                    {
                        _lifecycle.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SensorConnectException ex) when (ex.DeviceUnreachable)
                {
                    _log.Debug(DiagnosticCategory.Reconnect, $"Sensor not reachable: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _log.Warn(DiagnosticCategory.Reconnect, $"Reconnect attempt failed: {ex.Message}");
                }

                var delay = backoff.Next();
                _log.Debug(DiagnosticCategory.Reconnect, $"Next attempt in {delay.TotalSeconds:0.#}s (or sooner if the sensor advertises)");
                try
                {
                    await ResumeScannerAsync(scanner, ct).ConfigureAwait(false);
                    await seen.WaitAsync(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            if (scanner is not null)
            {
                try
                {
                    await scanner.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
            seen.Dispose();
        }
    }

    private async Task PauseScannerAsync(ISensorScanner? scanner)
    {
        if (scanner is null || !scanner.IsScanning)
        {
            return;
        }
        try
        {
            await scanner.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug(DiagnosticCategory.Reconnect, $"Could not pause scan: {ex.Message}");
        }
    }

    private async Task ResumeScannerAsync(ISensorScanner? scanner, CancellationToken ct)
    {
        if (scanner is null || scanner.IsScanning)
        {
            return;
        }
        try
        {
            await scanner.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug(DiagnosticCategory.Reconnect, $"Could not resume scan: {ex.Message}");
        }
    }

    private async Task StopReconnectLoopAsync()
    {
        var cts = _reconnectCts;
        var task = _reconnectTask;
        _reconnectCts = null;
        _reconnectTask = null;
        if (cts is null)
        {
            return;
        }
        await cts.CancelAsync().ConfigureAwait(false);
        if (task is not null)
        {
            try
            {
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
        cts.Dispose();
    }

    private async Task TearDownLinkAsync(string reason)
    {
        var link = _link;
        if (link is null)
        {
            return;
        }
        _link = null;
        link.DataNotified -= OnData;
        link.StatusNotified -= OnStatusNotified;
        link.ControlResponded -= OnControlResponded;
        link.Disconnected -= OnLinkDropped;
        try
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn(DiagnosticCategory.Connection, $"Error while closing link ({reason}): {ex.Message}");
        }
    }

    private void SetState(TrainerConnectionState state)
    {
        if (_state == state)
        {
            return;
        }
        _state = state;
        _log.Info(DiagnosticCategory.Connection, $"Connection state -> {state}");
        _publisher.Publish(TrainerTelemetry.NoData(DateTimeOffset.UtcNow, state, _pipeline.SystemId is null ? null : Convert.ToHexStringLower(_pipeline.SystemId),
            _targetName, _pipeline.IsSimulated));
        StateChanged?.Invoke(state);
    }

    /// <summary>Publishes a no-data sample when packets stop, and forces a reconnect when the link is silently dead.</summary>
    private async Task WatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.WatchdogInterval, ct).ConfigureAwait(false);
                var link = _link;
                if (link is null || _state != TrainerConnectionState.Connected)
                {
                    continue;
                }
                var last = _pipeline.LastPacketAt;
                if (last is null)
                {
                    continue;
                }
                var silence = DateTimeOffset.UtcNow - last.Value;
                if (silence > _options.DeadLinkTimeout)
                {
                    _log.Warn(DiagnosticCategory.Connection, $"No packets for {silence.TotalSeconds:0}s; treating link as dead");
                    OnLinkDropped("packet timeout");
                    await Task.Delay(_options.DeadLinkTimeout, ct).ConfigureAwait(false);
                }
                else if (silence > _options.StaleDataTimeout && !_staleReported)
                {
                    _staleReported = true;
                    _log.Warn(DiagnosticCategory.Connection, $"No packets for {silence.TotalSeconds:0.#}s; values marked stale");
                    _publisher.Publish(TrainerTelemetry.NoData(DateTimeOffset.UtcNow, _state, _pipeline.SystemId is null ? null : Convert.ToHexStringLower(_pipeline.SystemId),
                        _targetName, _pipeline.IsSimulated));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error(DiagnosticCategory.App, $"Watchdog stopped: {ex}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopReconnectLoopAsync().ConfigureAwait(false);
            await TearDownLinkAsync("shutdown").ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
        if (_watchdogTask is not null)
        {
            try
            {
                await _watchdogTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
        _disposeCts.Dispose();
    }
}
