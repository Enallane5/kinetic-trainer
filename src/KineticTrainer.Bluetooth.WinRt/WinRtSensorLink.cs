using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Core.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace KineticTrainer.Bluetooth.WinRt;

/// <summary>
/// One connected sensor. Characteristic roles per protocol:
///   inRide custom : data = Power E9410101 (notify), status = Configuration E9410104 (read/notify), control = E9410102 (write), read = Configuration
///   FTMS          : data = Indoor Bike Data 2AD2 (notify), status = Fitness Machine Status 2ADA (notify),
///                   control = Control Point 2AD9 (write + indicate), read = Fitness Machine Feature 2ACC
/// </summary>
public sealed class WinRtSensorLink : ISensorLink
{
    private readonly BluetoothLEDevice _device;
    private readonly GattSession _session;
    private readonly IReadOnlyList<GattDeviceService> _services;
    private readonly GattCharacteristic _data;
    private readonly GattCharacteristic? _status;
    private readonly GattCharacteristic? _control;
    private readonly GattCharacteristic? _read;
    private readonly IDiagnosticLog _log;
    private int _disconnectRaised;
    private bool _disposed;

    internal WinRtSensorLink(BluetoothLEDevice device, GattSession session, IReadOnlyList<GattDeviceService> services,
        TrainerProtocolKind protocol, GattCharacteristic data, GattCharacteristic? status, GattCharacteristic? control,
        GattCharacteristic? read, SensorDeviceInfo info, IDiagnosticLog log)
    {
        _device = device;
        _session = session;
        _services = services;
        Protocol = protocol;
        _data = data;
        _status = status;
        _control = control;
        _read = read;
        _log = log;
        DeviceInfo = info;
    }

    public ulong Address => _device.BluetoothAddress;

    public SensorDeviceInfo DeviceInfo { get; }

    public bool IsConnected => !_disposed && _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public TrainerProtocolKind Protocol { get; }

    public Core.Settings.SensorAddressType AddressType => _device.BluetoothAddressType switch
    {
        BluetoothAddressType.Public => Core.Settings.SensorAddressType.Public,
        BluetoothAddressType.Random => Core.Settings.SensorAddressType.Random,
        _ => Core.Settings.SensorAddressType.Unspecified,
    };

    public bool StatusSupportsNotify { get; internal set; }

    public event Action<byte[], DateTimeOffset>? DataNotified;

    public event Action<byte[], DateTimeOffset>? StatusNotified;

    public event Action<byte[], DateTimeOffset>? ControlResponded;

    public event Action<string>? Disconnected;

    internal void Attach()
    {
        _data.ValueChanged += OnDataValueChanged;
        if (_status is not null)
        {
            _status.ValueChanged += OnStatusValueChanged;
        }
        if (_control is not null)
        {
            _control.ValueChanged += OnControlValueChanged;
        }
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        _session.SessionStatusChanged += OnSessionStatusChanged;
    }

    private void OnDataValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            DataNotified?.Invoke(WinRtSensorConnector.ToArray(args.CharacteristicValue), args.Timestamp);
        }
        catch (Exception ex)
        {
            _log.Error(DiagnosticCategory.Gatt, $"Data notification handler error: {ex}");
        }
    }

    private void OnStatusValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            StatusNotified?.Invoke(WinRtSensorConnector.ToArray(args.CharacteristicValue), args.Timestamp);
        }
        catch (Exception ex)
        {
            _log.Error(DiagnosticCategory.Gatt, $"Status notification handler error: {ex}");
        }
    }

    private void OnControlValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            ControlResponded?.Invoke(WinRtSensorConnector.ToArray(args.CharacteristicValue), args.Timestamp);
        }
        catch (Exception ex)
        {
            _log.Error(DiagnosticCategory.Gatt, $"Control response handler error: {ex}");
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        _log.Info(DiagnosticCategory.Connection, $"Windows connection status: {sender.ConnectionStatus}");
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            RaiseDisconnected("Bluetooth connection lost");
        }
    }

    private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        _log.Debug(DiagnosticCategory.Connection, $"GATT session status: {args.Status} error={args.Error}");
        if (args.Status == GattSessionStatus.Closed)
        {
            RaiseDisconnected($"GATT session closed ({args.Error})");
        }
    }

    private void RaiseDisconnected(string reason)
    {
        if (_disposed || Interlocked.Exchange(ref _disconnectRaised, 1) != 0)
        {
            return;
        }
        Disconnected?.Invoke(reason);
    }

    public async Task<byte[]> ReadConfigurationAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_read is null)
        {
            throw new NotSupportedException("This sensor has no readable configuration characteristic.");
        }
        var r = await _read.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
        if (r.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"Configuration read failed: {r.Status}{WinRtSensorConnector.ProtocolError(r.ProtocolError)}");
        }
        return WinRtSensorConnector.ToArray(r.Value);
    }

    public async Task WriteControlPointAsync(byte[] payload, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_control is null)
        {
            throw new NotSupportedException("This sensor has no control point characteristic.");
        }
        var buffer = CryptographicBuffer.CreateFromByteArray(payload);
        var r = await _control.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithResponse).AsTask(ct).ConfigureAwait(false);
        if (r.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"Control point write failed: {r.Status}{WinRtSensorConnector.ProtocolError(r.ProtocolError)}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new InvalidOperationException("The sensor link is closed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _data.ValueChanged -= OnDataValueChanged;
        if (_status is not null)
        {
            _status.ValueChanged -= OnStatusValueChanged;
        }
        if (_control is not null)
        {
            _control.ValueChanged -= OnControlValueChanged;
        }
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _session.SessionStatusChanged -= OnSessionStatusChanged;

        // Best effort: tell the sensor to stop notifying so it does not keep the radio busy. Bounded so a dead link
        // cannot stall shutdown.
        if (_device.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _data.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
        foreach (var s in _services)
        {
            try { s.Dispose(); } catch { /* ignored */ }
        }
        try
        {
            _session.MaintainConnection = false;
            _session.Dispose();
        }
        catch { /* ignored */ }
        try { _device.Dispose(); } catch { /* ignored */ }
        _log.Info(DiagnosticCategory.Connection, "Sensor link closed");
    }
}
