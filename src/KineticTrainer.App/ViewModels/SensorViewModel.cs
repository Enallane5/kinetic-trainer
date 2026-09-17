using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Bluetooth.WinRt;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Settings;
using KineticTrainer.Core.Telemetry;

namespace KineticTrainer.App.ViewModels;

public sealed record CalibrationRow(string Date, string Outcome, string Result, string Window, Microsoft.UI.Xaml.Media.SolidColorBrush Brush);

public sealed partial class DeviceItem : ObservableObject
{
    public DeviceItem(DiscoveredSensor s, IRelayCommand<DeviceItem?> connectCommand)
    {
        Address = s.Address;
        AddressText = s.AddressText;
        IsSimulated = s.IsSimulated;
        ConnectCommand = connectCommand;
        Update(s);
    }

    /// <summary>The owning view model's connect command, so the row's button can bind to it.</summary>
    public IRelayCommand<DeviceItem?> ConnectCommand { get; }

    public ulong Address { get; }

    public SensorAddressType AddressType { get; private set; }

    public string AddressText { get; }

    public bool IsSimulated { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _rssiText = "";
    [ObservableProperty] private bool _looksLikeInRide;
    [ObservableProperty] private DateTimeOffset _lastSeen;
    [ObservableProperty] private string _badge = "";

    public void Update(DiscoveredSensor s)
    {
        if (s.AddressType != SensorAddressType.Unspecified)
        {
            AddressType = s.AddressType;
        }
        Name = s.Name ?? (LooksLikeInRide ? "(trainer, no name yet)" : "(unnamed)");
        RssiText = $"{s.Rssi} dBm";
        if (!LooksLikeInRide && s.LooksLikeInRide)
        {
            LooksLikeInRide = true;
        }
        var badge = IsSimulated ? "SIMULATED"
            : s.AdvertisesFitnessMachineService ? "inRide (FTMS)"
            : s.AdvertisesInRideService ? "inRide 2"
            : LooksLikeInRide ? "inRide" : "";
        if (badge.Length > Badge.Length)
        {
            Badge = badge;
        }
        LastSeen = s.LastSeen;
    }
}

public sealed partial class SensorViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly List<ISensorScanner> _scanners = [];
    private readonly Dictionary<ulong, DeviceItem> _byAddress = [];
    private CancellationTokenSource? _scanCts;
    private DispatcherQueueTimerWrapper? _pruneTimer;

    public SensorViewModel(AppServices services)
    {
        _services = services;
    }

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    /// <summary>Recent spin-down calibrations, newest first.</summary>
    public ObservableCollection<CalibrationRow> Calibrations { get; } = [];

    public IReadOnlyList<string> RateOptions { get; } = ["1 Hz (default)", "2 Hz", "4 Hz"];

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _showAllDevices;
    [ObservableProperty] private string _scanButtonText = "Scan for sensors";
    [ObservableProperty] private string _bluetoothStatus = "";
    [ObservableProperty] private string _rememberedText = "No sensor remembered yet.";
    [ObservableProperty] private string _connectionText = "Disconnected";
    [ObservableProperty] private string _protocolText = "";
    [ObservableProperty] private bool _isInRideProtocol;
    [ObservableProperty] private string _deviceInfoText = "";
    [ObservableProperty] private string _configurationText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _selectedRateIndex;
    [ObservableProperty] private string _rateStatus = "";
    [ObservableProperty] private string _calibrationSummary = "";

    public async Task LoadCalibrationsAsync()
    {
        Calibrations.Clear();
        if (_services.Rides is not { } rides)
        {
            CalibrationSummary = "Calibration history needs the local database.";
            return;
        }
        try
        {
            var unit = _services.Settings.SpeedUnit;
            foreach (var c in await rides.ListCalibrationsAsync(15))
            {
                var window = c.TargetLowMps is { } lo && c.TargetHighMps is { } hi
                    ? $"{Core.Units.UnitConversions.FromMetersPerSecond(lo, unit):0}-{Core.Units.UnitConversions.FromMetersPerSecond(hi, unit):0} {Core.Units.UnitConversions.Label(unit)}"
                    : "";
                Calibrations.Add(new CalibrationRow(
                    c.PerformedAt.ToLocalTime().ToString("d MMM yyyy  h:mm tt"),
                    c.Succeeded ? "OK" : "FAILED",
                    c.Result ?? "",
                    window,
                    new Microsoft.UI.Xaml.Media.SolidColorBrush(c.Succeeded ? Services.KineticColors.Accent : Services.KineticColors.Danger)));
            }
            CalibrationSummary = Calibrations.Count == 0
                ? "No calibrations yet. Kinetic recommends a spin-down after a 10-minute warm-up, with the tyre at its usual pressure."
                : "";
        }
        catch (Exception ex)
        {
            CalibrationSummary = $"Could not load calibrations: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Calibrate()
    {
        _services.Navigate("ride");
        _services.Ride.StartCalibrationCommand.Execute(null);
    }

    public void Start()
    {
        _services.Session.StateChanged += s => _services.Dispatcher.TryEnqueue(() => ApplyState(s));
        _services.Pipeline.ConfigurationUpdated += text => _services.Dispatcher.TryEnqueue(() => ConfigurationText = text);
        _services.Pipeline.ControlResponse += text => _services.Dispatcher.TryEnqueue(() => RateStatus = text);
        _services.SettingsChanged += _ => _services.Dispatcher.TryEnqueue(UpdateRemembered);
        UpdateRemembered();
        ApplyState(_services.Session.State);
        _ = CheckBluetoothAsync();
    }

    public void Stop() => _ = StopScanAsync();

    private async Task CheckBluetoothAsync()
    {
        var problem = await WinRtSensorPlatform.CheckAdapterAsync();
        _services.Dispatcher.TryEnqueue(() => BluetoothStatus = problem ?? "Bluetooth LE ready.");
    }

    private void UpdateRemembered()
    {
        var saved = _services.Settings.LastSensor;
        RememberedText = saved is null
            ? "No sensor remembered yet. Connect once and it will reconnect automatically next time."
            : $"Remembered: {saved.Name ?? "inRide"} {DiscoveredSensor.FormatAddress(saved.BluetoothAddress)} " +
              $"({saved.AddressType} address, last connected {saved.LastConnectedUtc.ToLocalTime():g})";
    }

    private void ApplyState(TrainerConnectionState state)
    {
        IsConnected = state == TrainerConnectionState.Connected;
        ConnectionText = state.ToString();
        var info = _services.Session.DeviceInfo;
        var protocol = _services.Session.Protocol;
        IsInRideProtocol = IsConnected && protocol == TrainerProtocolKind.InRideCustom;
        ProtocolText = !IsConnected ? "" : protocol switch
        {
            TrainerProtocolKind.FitnessMachine => "Protocol: Bluetooth Fitness Machine (FTMS) — standard Indoor Bike Data",
            TrainerProtocolKind.InRideCustom => "Protocol: Kinetic inRide 2 custom (E9410100)",
            _ => "",
        };
        DeviceInfoText = info is null ? "" :
            $"Name: {info.Name ?? "?"}\nModel: {info.ModelNumber ?? "?"}   Manufacturer: {info.ManufacturerName ?? "?"}\n" +
            $"Firmware: {info.FirmwareRevision ?? "?"}   Hardware: {info.HardwareRevision ?? "?"}   Battery: {(info.BatteryPercent is { } b ? b + "%" : "?")}\n" +
            $"System ID: {info.SystemIdHex ?? "(none)"} ({info.SystemId?.Length ?? 0} bytes)";
        if (!IsConnected)
        {
            ConfigurationText = "";
            RateStatus = "";
        }
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ApplyRateCommand.NotifyCanExecuteChanged();
        ReadConfigurationCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ToggleScanAsync()
    {
        if (IsScanning)
        {
            await StopScanAsync();
            return;
        }
        ErrorText = "";
        _scanCts = new CancellationTokenSource();
        var platforms = new List<ISensorPlatform> { _services.WinRt };
        if (_services.Settings.ShowSimulatedSensor)
        {
            platforms.Add(_services.Simulated);
        }
        foreach (var platform in platforms)
        {
            try
            {
                var scanner = platform.CreateScanner();
                scanner.SensorSeen += OnSensorSeen;
                await scanner.StartAsync(_scanCts.Token);
                _scanners.Add(scanner);
            }
            catch (Exception ex)
            {
                ErrorText = $"Scan failed ({platform.Name}): {ex.Message}";
            }
        }
        IsScanning = _scanners.Count > 0;
        ScanButtonText = IsScanning ? "Stop scanning" : "Scan for sensors";
        _pruneTimer ??= new DispatcherQueueTimerWrapper(_services.Dispatcher, TimeSpan.FromSeconds(2), Prune);
        _pruneTimer.Start();
    }

    private async Task StopScanAsync()
    {
        _scanCts?.Cancel();
        foreach (var s in _scanners)
        {
            s.SensorSeen -= OnSensorSeen;
            try
            {
                await s.DisposeAsync();
            }
            catch
            {
                // ignored
            }
        }
        _scanners.Clear();
        _pruneTimer?.Stop();
        _services.Dispatcher.TryEnqueue(() =>
        {
            IsScanning = false;
            ScanButtonText = "Scan for sensors";
        });
    }

    private void OnSensorSeen(DiscoveredSensor s)
    {
        _services.Dispatcher.TryEnqueue(() =>
        {
            if (_byAddress.TryGetValue(s.Address, out var item))
            {
                item.Update(s);
                return;
            }
            item = new DeviceItem(s, ConnectCommand);
            if (!item.LooksLikeInRide && !item.IsSimulated && !ShowAllDevices)
            {
                _byAddress[s.Address] = item; // tracked so the filter toggle can reveal it
                return;
            }
            _byAddress[s.Address] = item;
            Insert(item);
        });
    }

    private void Insert(DeviceItem item)
    {
        var index = 0;
        while (index < Devices.Count && (Devices[index].LooksLikeInRide || Devices[index].IsSimulated) && !(item.LooksLikeInRide || item.IsSimulated))
        {
            index++;
        }
        Devices.Insert(index, item);
    }

    partial void OnShowAllDevicesChanged(bool value)
    {
        Devices.Clear();
        foreach (var item in _byAddress.Values.OrderByDescending(d => d.LooksLikeInRide || d.IsSimulated).ThenBy(d => d.Name))
        {
            if (value || item.LooksLikeInRide || item.IsSimulated)
            {
                Devices.Add(item);
            }
        }
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20);
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (Devices[i].LastSeen < cutoff)
            {
                _byAddress.Remove(Devices[i].Address);
                Devices.RemoveAt(i);
            }
        }
    }

    private bool CanConnect(DeviceItem? item) => item is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(DeviceItem? item)
    {
        if (item is null)
        {
            return;
        }
        ErrorText = "";
        IsBusy = true;
        try
        {
            await StopScanAsync();
            await _services.Session.ConnectAsync(item.Address, item.AddressType, item.Name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorText = $"Could not connect: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ConnectCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task ConnectRememberedAsync()
    {
        if (_services.Settings.LastSensor is not { } saved)
        {
            return;
        }
        ErrorText = "";
        await _services.Session.ReconnectToSavedAsync(saved);
    }

    private bool CanDisconnect() => _services.Session.State != TrainerConnectionState.Disconnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        try
        {
            await _services.Session.DisconnectAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ForgetSensorAsync()
    {
        await _services.UpdateSettingsAsync(_services.Settings with { LastSensor = null });
        _services.Log.Info(DiagnosticCategory.Settings, "Remembered sensor cleared");
    }

    private bool CanUseLink() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanUseLink))]
    private async Task ApplyRateAsync()
    {
        var rate = SelectedRateIndex switch { 1 => SensorUpdateRate.TwoHz, 2 => SensorUpdateRate.FourHz, _ => SensorUpdateRate.OneHz };
        RateStatus = "Sending…";
        try
        {
            await _services.Session.SetUpdateRateAsync(rate, CancellationToken.None);
            RateStatus = "Command accepted by Windows; waiting for the sensor's answer in the next packet…";
            await _services.UpdateSettingsAsync(_services.Settings with { PreferredUpdateRate = rate });
        }
        catch (Exception ex)
        {
            RateStatus = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseLink))]
    private async Task ReadConfigurationAsync()
    {
        try
        {
            var text = await _services.Session.ReadConfigurationAsync(CancellationToken.None);
            ConfigurationText = text ?? "Configuration rejected — see Debug page.";
        }
        catch (Exception ex)
        {
            ConfigurationText = $"Read failed: {ex.Message}";
        }
    }
}

/// <summary>Small wrapper so view models can run periodic UI-thread work without touching XAML types directly.</summary>
public sealed class DispatcherQueueTimerWrapper
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    public DispatcherQueueTimerWrapper(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = interval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => tick();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}
