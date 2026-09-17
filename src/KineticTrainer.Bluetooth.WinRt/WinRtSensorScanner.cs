using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Protocol.InRide;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace KineticTrainer.Bluetooth.WinRt;

/// <summary>Reports every BLE advertiser; the UI highlights ones that look like an inRide.</summary>
public sealed class WinRtSensorScanner : ISensorScanner
{
    private readonly IDiagnosticLog _log;
    private BluetoothLEAdvertisementWatcher? _watcher;

    public WinRtSensorScanner(IDiagnosticLog log)
    {
        _log = log;
    }

    public event Action<DiscoveredSensor>? SensorSeen;

    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public Task StartAsync(CancellationToken ct)
    {
        if (_watcher is not null)
        {
            return Task.CompletedTask;
        }
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            // Active scanning requests the scan response, which is where the inRide's name usually is.
            ScanningMode = BluetoothLEScanningMode.Active,
            AllowExtendedAdvertisements = false,
        };
        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        _watcher = watcher;
        try
        {
            watcher.Start();
            _log.Info(DiagnosticCategory.Scan, "BLE advertisement scan started");
        }
        catch (Exception ex)
        {
            _watcher = null;
            _log.Error(DiagnosticCategory.Scan, $"BLE scan could not start: {ex.Message}");
            throw;
        }
        ct.Register(() => _ = StopAsync());
        return Task.CompletedTask;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            var name = args.Advertisement.LocalName;
            var hasService = false;
            var hasFtms = false;
            foreach (var uuid in args.Advertisement.ServiceUuids)
            {
                if (uuid == InRideUuids.PowerService)
                {
                    hasService = true;
                }
                else if (uuid == KineticTrainer.Protocol.Ftms.FtmsUuids.FitnessMachineService)
                {
                    hasFtms = true;
                }
            }
            var addressType = args.BluetoothAddressType switch
            {
                BluetoothAddressType.Public => Core.Settings.SensorAddressType.Public,
                BluetoothAddressType.Random => Core.Settings.SensorAddressType.Random,
                _ => Core.Settings.SensorAddressType.Unspecified,
            };
            SensorSeen?.Invoke(new DiscoveredSensor(args.BluetoothAddress, string.IsNullOrWhiteSpace(name) ? null : name,
                args.RawSignalStrengthInDBm, hasService, args.Timestamp, AddressType: addressType, AdvertisesFitnessMachineService: hasFtms));
        }
        catch (Exception ex)
        {
            _log.Warn(DiagnosticCategory.Scan, $"Advertisement handling error: {ex.Message}");
        }
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error == BluetoothError.Success)
        {
            _log.Info(DiagnosticCategory.Scan, "BLE advertisement scan stopped");
        }
        else
        {
            _log.Warn(DiagnosticCategory.Scan, $"BLE advertisement scan stopped with error: {args.Error}");
        }
    }

    public Task StopAsync()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
        {
            return Task.CompletedTask;
        }
        try
        {
            watcher.Received -= OnReceived;
            watcher.Stop();
        }
        catch (Exception ex)
        {
            _log.Warn(DiagnosticCategory.Scan, $"Error stopping scan: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
