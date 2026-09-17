using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KineticTrainer.Bluetooth.Pipeline;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Core.Units;
using KineticTrainer.Protocol.InRide;
using Windows.ApplicationModel.DataTransfer;

namespace KineticTrainer.App.ViewModels;

public sealed record PacketRow(string Time, string Raw, string Plain, string Power, string Cadence, string Roller, string Speed, string State, string Cmd, string Extra);

public sealed record EventRow(string Time, string Severity, string Category, string Message, string Raw)
{
    public string Line => Raw.Length == 0 ? $"{Time} {Severity,-7} {Category,-14} {Message}" : $"{Time} {Severity,-7} {Category,-14} {Message} | {Raw}";
}

/// <summary>Protocol debug view: raw packets, parsed values, every diagnostic event, counters, and manual control-point writes.</summary>
public sealed partial class DebugViewModel : ObservableObject
{
    private const int MaxPackets = 300;
    private const int MaxEvents = 1500;

    private readonly AppServices _services;
    private readonly List<EventRow> _allEvents = [];
    private readonly CancellationTokenSource _cts = new();
    private DispatcherQueueTimerWrapper? _statsTimer;

    public DebugViewModel(AppServices services)
    {
        _services = services;
    }

    public ObservableCollection<PacketRow> Packets { get; } = [];

    public ObservableCollection<EventRow> Events { get; } = [];

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _showDebugEvents;
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private string _logFileText = "";
    [ObservableProperty] private string _controlPointHex = "";
    [ObservableProperty] private string _commandStatus = "";
    [ObservableProperty] private bool _simVisible;
    [ObservableProperty] private double _simSpeedKph = 32;
    [ObservableProperty] private double _simCadence = 88;
    [ObservableProperty] private bool _simMalformed;
    [ObservableProperty] private bool _simUnreachable;

    public void Start()
    {
        _services.Pipeline.PacketAccepted += OnPacket;
        _services.Session.StateChanged += _ => _services.Dispatcher.TryEnqueue(UpdateSimVisibility);
        _ = Task.Run(() => PumpEventsAsync(_cts.Token));
        _statsTimer = new DispatcherQueueTimerWrapper(_services.Dispatcher, TimeSpan.FromMilliseconds(500), UpdateStats);
        _statsTimer.Start();
        foreach (var e in _services.Log.Recent())
        {
            AddEvent(e);
        }
        UpdateSimVisibility();
    }

    private void OnPacket(InterpretedPacket p, TrainerTelemetry t)
    {
        if (IsPaused)
        {
            return;
        }
        var row = new PacketRow(
            t.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
            p.RawHex,
            p.DecodedHex ?? "",
            p.PowerWatts?.ToString() ?? "-",
            p.InRide is { } ir ? $"{ir.CadenceRpm:0.0} ({ir.Packet.CadenceRaw})" : p.CadenceRpm is { } c ? c.ToString("0.0") : "-",
            p.RollerRpm is { } r ? r.ToString("0") : "-",
            p.SpeedMetersPerSecond is { } s ? $"{UnitConversions.ToKph(s):0.0} kph / {UnitConversions.ToMph(s):0.0} mph" : "-",
            p.StateText,
            p.CommandText,
            p.Detail);
        _services.Dispatcher.TryEnqueue(() =>
        {
            Packets.Insert(0, row);
            while (Packets.Count > MaxPackets)
            {
                Packets.RemoveAt(Packets.Count - 1);
            }
        });
    }

    private async Task PumpEventsAsync(CancellationToken ct)
    {
        using var sub = _services.Log.Subscribe("debug-view", 4096);
        try
        {
            while (await sub.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var batch = new List<DiagnosticEvent>();
                while (sub.Reader.TryRead(out var e))
                {
                    batch.Add(e);
                }
                _services.Dispatcher.TryEnqueue(() =>
                {
                    foreach (var e in batch)
                    {
                        AddEvent(e);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AddEvent(DiagnosticEvent e)
    {
        if (e.Category == DiagnosticCategory.PowerPacket)
        {
            return; // packets have their own table
        }
        var row = new EventRow(e.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"), e.Severity.ToString(), e.Category.ToString(), e.Message, e.RawHex ?? "");
        _allEvents.Add(row);
        if (_allEvents.Count > MaxEvents)
        {
            _allEvents.RemoveAt(0);
        }
        if (IsPaused || (!ShowDebugEvents && e.Severity == DiagnosticSeverity.Debug))
        {
            return;
        }
        Events.Insert(0, row);
        while (Events.Count > MaxEvents)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    partial void OnShowDebugEventsChanged(bool value) => Rebuild();

    private void Rebuild()
    {
        Events.Clear();
        for (var i = _allEvents.Count - 1; i >= 0; i--)
        {
            var r = _allEvents[i];
            if (ShowDebugEvents || r.Severity != nameof(DiagnosticSeverity.Debug))
            {
                Events.Add(r);
            }
        }
    }

    private void UpdateStats()
    {
        var p = _services.Pipeline;
        var rate = p.PacketRateHz;
        var median = p.PacketMedianInterval;
        var last = p.LastPacketAt is { } t ? $"{(DateTimeOffset.UtcNow - t).TotalSeconds:0.0}s ago" : "never";
        StatsText = $"Accepted {p.AcceptedPackets}   Rejected {p.RejectedPackets}   Rate {(rate is { } r ? $"{r:0.00} Hz" : "—")}   " +
                    $"Median interval {(median is { } m ? $"{m.TotalMilliseconds:0} ms" : "—")}   Last packet {last}   " +
                    $"System ID {(p.SystemId is null ? "(none)" : Convert.ToHexStringLower(p.SystemId))}";
        LogFileText = _services.LogFilePath is { } f ? $"Log file: {f}" : $"Log file off (folder {_services.LogDirectory})";
    }

    private void UpdateSimVisibility()
    {
        SimVisible = _services.Settings.ShowSimulatedSensor;
        var profile = _services.Simulated.Profile;
        SimSpeedKph = profile.TargetSpeedKph;
        SimCadence = profile.CadenceRpm;
        SimMalformed = profile.EmitMalformedPackets;
        SimUnreachable = profile.Unreachable;
    }

    partial void OnSimSpeedKphChanged(double value) => _services.Simulated.Profile.TargetSpeedKph = value;

    partial void OnSimCadenceChanged(double value) => _services.Simulated.Profile.CadenceRpm = value;

    partial void OnSimMalformedChanged(bool value) => _services.Simulated.Profile.EmitMalformedPackets = value;

    partial void OnSimUnreachableChanged(bool value) => _services.Simulated.Profile.Unreachable = value;

    [RelayCommand]
    private void SimDrop() => _services.Simulated.ActiveLink?.SimulateDrop();

    [RelayCommand]
    private void Clear()
    {
        Packets.Clear();
        Events.Clear();
        _allEvents.Clear();
    }

    [RelayCommand]
    private void CopyEvents()
    {
        var sb = new StringBuilder();
        foreach (var r in Events.Reverse())
        {
            sb.AppendLine(r.Line);
        }
        var package = new DataPackage();
        package.SetText(sb.ToString());
        Clipboard.SetContent(package);
    }

    [RelayCommand]
    private void CopyPackets()
    {
        var sb = new StringBuilder();
        sb.AppendLine("time\traw\tplain\tpower\tcadence\troller\tspeed\tstate\tcmd\textra");
        foreach (var r in Packets.Reverse())
        {
            sb.AppendLine($"{r.Time}\t{r.Raw}\t{r.Plain}\t{r.Power}\t{r.Cadence}\t{r.Roller}\t{r.Speed}\t{r.State}\t{r.Cmd}\t{r.Extra}");
        }
        var package = new DataPackage();
        package.SetText(sb.ToString());
        Clipboard.SetContent(package);
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_services.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _services.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CommandStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task WriteControlPointAsync()
    {
        try
        {
            var hex = ControlPointHex.Replace(" ", "").Replace("-", "").Replace(":", "");
            var bytes = Convert.FromHexString(hex);
            await _services.Session.WriteControlPointAsync(bytes, CancellationToken.None);
            CommandStatus = $"Wrote {bytes.Length} bytes; watch the next packet's command result.";
        }
        catch (Exception ex)
        {
            CommandStatus = $"Write failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void FillConfigureCommand(string? rate)
    {
        var systemId = _services.Session.DeviceInfo?.SystemId ?? _services.Pipeline.SystemId;
        if (systemId is null || systemId.Length != InRideConstants.SystemIdLength)
        {
            CommandStatus = "No 6-byte System ID available; connect first.";
            return;
        }
        var r = rate switch { "2" => InRideUpdateRate.Millis500, "4" => InRideUpdateRate.Millis250, _ => InRideUpdateRate.Millis1000 };
        ControlPointHex = Convert.ToHexStringLower(InRideCommands.ConfigureSensor(systemId, r));
        CommandStatus = $"Configure-sensor command for {r.Hz()} Hz (not sent yet)";
    }
}
