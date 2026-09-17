using CommunityToolkit.Mvvm.ComponentModel;
using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Core.Telemetry;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace KineticTrainer.App.ViewModels;

/// <summary>Always-visible connection indicator in the window header.</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    private readonly AppServices _services;
    private TelemetryUiFeed? _feed;

    [ObservableProperty] private string _stateText = "Disconnected";
    [ObservableProperty] private SolidColorBrush _stateBrush = new(Colors.Gray);
    [ObservableProperty] private string _sensorText = "No sensor";
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private string _bridgeText = "";
    [ObservableProperty] private SolidColorBrush _riderBrush = new(Services.KineticColors.Accent);
    [ObservableProperty] private string _riderInitials = "";
    [ObservableProperty] private string _riderText = "";

    public StatusViewModel(AppServices services)
    {
        _services = services;
        ApplyRider(services.ActiveProfile);
        services.ActiveProfileChanged += ApplyRider;
    }

    private void ApplyRider(Core.Profiles.RiderProfile p)
    {
        RiderBrush = new SolidColorBrush(Services.KineticColors.Hex(p.Color));
        RiderInitials = p.Initials;
        RiderText = p.Ftp is { } f ? $"{p.Name}  ·  {f} W" : p.Name;
    }

    public void Start()
    {
        _services.Session.StateChanged += state => _services.Dispatcher.TryEnqueue(() => Apply(state));
        _services.Bridge.StatusChanged += () => _services.Dispatcher.TryEnqueue(UpdateBridge);
        _services.SettingsChanged += _ => _services.Dispatcher.TryEnqueue(UpdateBridge);
        _feed = new TelemetryUiFeed(_services.Telemetry, _services.Dispatcher, "status", OnTelemetry);
        _feed.Start();
        Apply(_services.Session.State);
        UpdateBridge();
    }

    private void OnTelemetry(TrainerTelemetry t)
    {
        var rate = _services.Pipeline.PacketRateHz;
        RateText = t.IsDataValid && rate is { } r ? $"{r:0.0} Hz" : "";
        UpdateSensorText();
    }

    private void Apply(TrainerConnectionState state)
    {
        StateText = state switch
        {
            TrainerConnectionState.Disconnected => "Disconnected",
            TrainerConnectionState.Searching => "Searching…",
            TrainerConnectionState.Connecting => "Connecting…",
            TrainerConnectionState.Connected => "Connected",
            TrainerConnectionState.Reconnecting => "Reconnecting…",
            _ => state.ToString(),
        };
        StateBrush = new SolidColorBrush(state switch
        {
            TrainerConnectionState.Connected => Services.KineticColors.Accent,
            TrainerConnectionState.Reconnecting => Services.KineticColors.Danger,
            TrainerConnectionState.Connecting or TrainerConnectionState.Searching => Services.KineticColors.Warning,
            _ => Services.KineticColors.TextSecondary,
        });
        if (state != TrainerConnectionState.Connected)
        {
            RateText = "";
        }
        UpdateSensorText();
    }

    private void UpdateSensorText()
    {
        var session = _services.Session;
        var name = session.DeviceInfo?.Name ?? session.TargetName;
        var addr = session.ConnectedAddress ?? session.TargetAddress;
        var sim = _services.Pipeline.IsSimulated && session.State == TrainerConnectionState.Connected ? " (SIMULATED)" : "";
        SensorText = addr is null ? "No sensor" : $"{name ?? "inRide"} {DiscoveredSensor.FormatAddress(addr.Value)}{sim}";
    }

    private void UpdateBridge()
    {
        var b = _services.Bridge;
        BridgeText = !_services.Settings.SimulatorBridgeEnabled ? "Bridge off"
            : b.IsRunning ? $"Bridge 127.0.0.1:{b.Port} ({b.ClientCount} client{(b.ClientCount == 1 ? "" : "s")})"
            : $"Bridge error: {b.LastError}";
    }
}
