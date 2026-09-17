using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KineticTrainer.App.Services;
using KineticTrainer.Core.Settings;
using KineticTrainer.Core.Units;

namespace KineticTrainer.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Load(services.Settings);
        services.SettingsChanged += s => services.Dispatcher.TryEnqueue(() => SpokenCues = s.SpokenCuesEnabled);
    }

    public IReadOnlyList<string> SpeedUnits { get; } = ["mph", "km/h"];

    public IReadOnlyList<string> DistanceUnits { get; } = ["miles (weight in lb)", "kilometres (weight in kg)"];

    public IReadOnlyList<string> RateOptions { get; } = ["Leave sensor as is", "1 Hz", "2 Hz", "4 Hz"];

    public IReadOnlyList<string> GraphOptions { get; } = AppSettings.PowerGraphChoices.Select(m => $"{m} minutes").ToList();

    [ObservableProperty] private double _rollerMm;
    [ObservableProperty] private int _speedUnitIndex;
    [ObservableProperty] private int _distanceUnitIndex;
    [ObservableProperty] private int _rateIndex;
    [ObservableProperty] private bool _autoReconnect;
    [ObservableProperty] private bool _bridgeEnabled;
    [ObservableProperty] private double _bridgePort;
    [ObservableProperty] private bool _writeLogFile;
    [ObservableProperty] private bool _showSimulated;
    [ObservableProperty] private bool _ignoreSystemId;
    [ObservableProperty] private string _exportFolder = "";
    [ObservableProperty] private bool _audioCues;
    [ObservableProperty] private bool _spokenCues;
    [ObservableProperty] private double _tolerance;
    [ObservableProperty] private int _graphIndex;
    [ObservableProperty] private bool _colorByZone;
    [ObservableProperty] private bool _intervalPopups;
    [ObservableProperty] private string _message = "";

    public string RollerHelp =>
        $"Default {AppSettings.KineticRollerCircumferenceMeters * 1000:0.000} mm = Kinetic's RollerCircumferenceKM constant " +
        "(54 mm nominal diameter, from the SDK's KineticConstants.h). Ground speed = circumference × roller RPM.";

    public string DatabaseText => _services.Database is null ? "Local database unavailable: settings will not persist." : $"Database: {_services.Database.Path}";

    public string VersionText => $"Kinetic Trainer {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}  ·  works fully offline  ·  logs: {_services.LogDirectory}";

    private void Load(AppSettings s)
    {
        RollerMm = Math.Round(s.RollerCircumferenceMeters * 1000, 3);
        SpeedUnitIndex = s.SpeedUnit == SpeedUnit.KilometersPerHour ? 1 : 0;
        DistanceUnitIndex = s.DistanceUnit == DistanceUnit.Kilometers ? 1 : 0;
        RateIndex = s.PreferredUpdateRate switch { SensorUpdateRate.OneHz => 1, SensorUpdateRate.TwoHz => 2, SensorUpdateRate.FourHz => 3, _ => 0 };
        AutoReconnect = s.AutoReconnect;
        BridgeEnabled = s.SimulatorBridgeEnabled;
        BridgePort = s.SimulatorBridgePort;
        WriteLogFile = s.WriteDiagnosticLogFile;
        ShowSimulated = s.ShowSimulatedSensor;
        IgnoreSystemId = s.IgnoreSystemIdValidation;
        ExportFolder = string.IsNullOrWhiteSpace(s.ExportFolder) ? Recording.RideExporter.DefaultFolder : s.ExportFolder;
        AudioCues = s.AudioCuesEnabled;
        SpokenCues = s.SpokenCuesEnabled;
        Tolerance = s.TargetTolerancePercent;
        GraphIndex = Math.Max(0, AppSettings.PowerGraphChoices.ToList().IndexOf(s.PowerGraphMinutes));
        ColorByZone = s.ColorProfilesByZone;
        IntervalPopups = s.ShowIntervalPopups;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var updated = _services.Settings with
        {
            RollerCircumferenceMeters = RollerMm / 1000.0,
            SpeedUnit = SpeedUnitIndex == 1 ? SpeedUnit.KilometersPerHour : SpeedUnit.MilesPerHour,
            DistanceUnit = DistanceUnitIndex == 1 ? DistanceUnit.Kilometers : DistanceUnit.Miles,
            PreferredUpdateRate = RateIndex switch { 1 => SensorUpdateRate.OneHz, 2 => SensorUpdateRate.TwoHz, 3 => SensorUpdateRate.FourHz, _ => null },
            AutoReconnect = AutoReconnect,
            SimulatorBridgeEnabled = BridgeEnabled,
            SimulatorBridgePort = double.IsNaN(BridgePort) ? AppSettings.DefaultBridgePort : (int)BridgePort,
            WriteDiagnosticLogFile = WriteLogFile,
            ShowSimulatedSensor = ShowSimulated,
            IgnoreSystemIdValidation = IgnoreSystemId,
            ExportFolder = string.IsNullOrWhiteSpace(ExportFolder) || ExportFolder == Recording.RideExporter.DefaultFolder ? null : ExportFolder.Trim(),
            AudioCuesEnabled = AudioCues,
            SpokenCuesEnabled = SpokenCues,
            TargetTolerancePercent = (int)Math.Round(Tolerance),
            PowerGraphMinutes = AppSettings.PowerGraphChoices[Math.Clamp(GraphIndex, 0, AppSettings.PowerGraphChoices.Count - 1)],
            ColorProfilesByZone = ColorByZone,
            ShowIntervalPopups = IntervalPopups,
        };
        var errors = updated.Validate();
        if (errors.Count > 0)
        {
            Message = string.Join(" ", errors);
            return;
        }
        if (updated.ExportFolder is { } folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                Message = $"The export folder cannot be used: {ex.Message}";
                return;
            }
        }
        await _services.UpdateSettingsAsync(updated);
        Message = _services.Bridge.IsRunning || !updated.SimulatorBridgeEnabled
            ? "Saved."
            : $"Saved, but the bridge could not start: {_services.Bridge.LastError}";
    }

    [RelayCommand]
    private void ResetRoller() => RollerMm = Math.Round(AppSettings.KineticRollerCircumferenceMeters * 1000, 3);

    [RelayCommand]
    private async Task BrowseExportFolderAsync()
    {
        try
        {
            if (await WindowServices.PickFolderAsync() is { } folder)
            {
                ExportFolder = folder.Path;
            }
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void TestAudio() => _services.Cues.Test();
}
