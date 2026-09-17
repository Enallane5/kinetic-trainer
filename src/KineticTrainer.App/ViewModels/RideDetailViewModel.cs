using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KineticTrainer.App.Services;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Units;
using KineticTrainer.Persistence;
using KineticTrainer.Workouts.Playback;
using KineticTrainer.Workouts.Timeline;
using KineticTrainer.Workouts.Zwo;
using Microsoft.UI.Xaml.Media;

namespace KineticTrainer.App.ViewModels;

public sealed record ComplianceRow(string Label, string Planned, string Target, string Actual, string OnTarget, SolidColorBrush Brush);

public sealed partial class RideDetailViewModel : ObservableObject
{
    private readonly AppServices _services;
    private StoredRide? _ride;

    public RideDetailViewModel(AppServices services)
    {
        _services = services;
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _editableTitle = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _speedUnit = "mph";
    [ObservableProperty] private bool _hasZones;
    [ObservableProperty] private bool _hasWorkout;
    [ObservableProperty] private bool _hasCompliance;
    [ObservableProperty] private string _complianceText = "";
    [ObservableProperty] private string _zonesNote = "";
    [ObservableProperty] private string _filesText = "";
    [ObservableProperty] private WorkoutTimeline? _workoutTimeline;
    [ObservableProperty] private int _chartFtp;
    [ObservableProperty] private bool _colorByZone = true;

    public ObservableCollection<SummaryTile> Tiles { get; } = [];
    public ObservableCollection<ZoneBar> Zones { get; } = [];
    public ObservableCollection<BestRow> Bests { get; } = [];
    public ObservableCollection<ComplianceRow> Compliance { get; } = [];

    public IReadOnlyList<SeriesDownsampler.Point> PowerSeries { get; private set; } = [];

    public IReadOnlyList<SeriesDownsampler.Point> CadenceSeries { get; private set; } = [];

    public IReadOnlyList<SeriesDownsampler.Point> SpeedSeries { get; private set; } = [];

    /// <summary>Ride power per elapsed second (for the workout overlay).</summary>
    public double?[] PowerBySecond { get; private set; } = [];

    public int? Ftp { get; private set; }

    public event Action? SeriesLoaded;

    public event Action? Deleted;

    public async Task LoadAsync(long id)
    {
        if (_services.Rides is null)
        {
            StatusText = "Ride history is unavailable.";
            return;
        }
        try
        {
            var ride = await _services.Rides.GetAsync(id);
            if (ride is null)
            {
                StatusText = "This ride no longer exists.";
                return;
            }
            _ride = ride;
            var settings = _services.Settings;
            var s = ride.Summary;
            Ftp = s.Ftp;
            ColorByZone = settings.ColorProfilesByZone;
            SpeedUnit = UnitConversions.Label(settings.SpeedUnit);
            Title = string.IsNullOrWhiteSpace(ride.Item.Title) ? "Free ride" : ride.Item.Title!;
            EditableTitle = ride.Item.Title ?? "";
            Subtitle = $"{ride.Item.StartedAt.ToLocalTime():dddd d MMMM yyyy, h:mm tt}  ·  {ride.Item.SensorName ?? "unknown sensor"}" +
                       (ride.Item.Simulated ? "  ·  SIMULATED" : "") + (s.Ftp is { } f ? $"  ·  FTP {f} W" : "  ·  no FTP recorded");

            Tiles.Clear();
            Tiles.Add(new("TIME", Format.Duration(s.ElapsedSeconds), ""));
            Tiles.Add(new("MOVING", Format.Duration(s.MovingSeconds), ""));
            Tiles.Add(new("DISTANCE", UnitConversions.FromMeters(s.DistanceMeters, settings.DistanceUnit).ToString("0.00"), UnitConversions.Label(settings.DistanceUnit)));
            Tiles.Add(new("AVG POWER", Format.Number(s.AveragePowerWatts), "W"));
            Tiles.Add(new("MAX POWER", s.MaxPowerWatts?.ToString() ?? "—", "W"));
            Tiles.Add(new("NORMALIZED", Format.Number(s.NormalizedPowerWatts), "W"));
            Tiles.Add(new("IF", Format.Number(s.IntensityFactor, "0.00"), ""));
            Tiles.Add(new("TSS", Format.Number(s.TrainingStressScore), ""));
            Tiles.Add(new("WORK", Format.Number(s.WorkKilojoules), "kJ"));
            Tiles.Add(new("AVG CADENCE", Format.Number(s.AverageCadenceRpm), "rpm"));
            Tiles.Add(new("MAX CADENCE", Format.Number(s.MaxCadenceRpm), "rpm"));
            Tiles.Add(new("AVG SPEED", s.AverageSpeedMetersPerSecond is { } av ? UnitConversions.FromMetersPerSecond(av, settings.SpeedUnit).ToString("0.0") : "—", SpeedUnit));
            Tiles.Add(new("MAX SPEED", s.MaxSpeedMetersPerSecond is { } mv ? UnitConversions.FromMetersPerSecond(mv, settings.SpeedUnit).ToString("0.0") : "—", SpeedUnit));

            Zones.Clear();
            foreach (var z in ZoneBar.From(s.ZoneSeconds?.ToList(), s.Ftp))
            {
                Zones.Add(z);
            }
            HasZones = Zones.Count > 0;
            ZonesNote = HasZones ? "" : "Time in zones needs an FTP. Set one on the Profile page; rides recorded after that show zones.";

            Bests.Clear();
            if (s.PowerBests is { } bests)
            {
                var weight = _services.ActiveProfile.WeightKg;
                foreach (var (d, w) in bests.OrderBy(b => b.Key))
                {
                    Bests.Add(new BestRow(PowerCurve.Label(d), $"{w:0} W", weight is > 0 ? $"{w / weight.Value:0.00} W/kg" : "", "", ride.Item.Id));
                }
            }

            FilesText = ride.Item.FitPath is { } fit ? $"{fit}\n{ride.Item.TcxPath}" : "No files were exported for this ride.";

            // Workout: planned profile and compliance.
            HasWorkout = false;
            WorkoutTimeline = null;
            if (ride.WorkoutZwo is { } zwo && ZwoParser.Parse(zwo).Workout is { } workout)
            {
                ChartFtp = s.Ftp ?? AppServices.FallbackFtp;
                WorkoutTimeline = new WorkoutTimeline(workout);
                HasWorkout = true;
            }
            Compliance.Clear();
            var compliance = WorkoutCompliance.FromJson(ride.ComplianceJson);
            HasCompliance = compliance is not null;
            if (compliance is not null)
            {
                if (compliance.Ftp is { } cf)
                {
                    ChartFtp = cf;
                }
                ComplianceText = $"{(compliance.Completed ? "Completed" : "Not completed")}  ·  rode {Format.Duration(compliance.RiddenSeconds)} of {Format.Duration(compliance.PlannedSeconds)}" +
                                 (compliance.OnTargetPercent is { } on ? $"  ·  {on:0}% of the time on target" : "") +
                                 (compliance.IntensityPercent != 100 ? $"  ·  intensity {compliance.IntensityPercent}%" : "") +
                                 (compliance.EstimatedFtp is { } est ? $"  ·  FTP test result {est} W" : "");
                foreach (var seg in compliance.Segments)
                {
                    var scored = seg.OnTargetSeconds + seg.UnderSeconds + seg.OverSeconds;
                    var fraction = seg.TargetWatts is { } tw && compliance.Ftp is > 0 ? tw / compliance.Ftp.Value : (double?)null;
                    Compliance.Add(new ComplianceRow(
                        seg.Label,
                        Format.Duration(seg.PlannedSeconds) + (seg.Skipped ? " (skipped)" : ""),
                        seg.TargetWatts is { } t ? $"{t:0} W" : "—",
                        seg.AverageWatts is { } a ? $"{a:0} W" : "—",
                        scored > 0 ? $"{seg.OnTargetSeconds / scored * 100:0}%" : "—",
                        fraction is { } fr ? KineticColors.ZoneBrush(Core.Zones.PowerZones.ZoneForFraction(fr).Number) : new SolidColorBrush(KineticColors.TextSecondary)));
                }
            }

            PowerSeries = ride.Samples.Where(x => x.PowerWatts is not null).Select(x => new SeriesDownsampler.Point(x.ElapsedSeconds, x.PowerWatts!.Value)).ToList();
            CadenceSeries = ride.Samples.Where(x => x.CadenceRpm is not null).Select(x => new SeriesDownsampler.Point(x.ElapsedSeconds, x.CadenceRpm!.Value)).ToList();
            SpeedSeries = ride.Samples.Where(x => x.SpeedMetersPerSecond is not null)
                .Select(x => new SeriesDownsampler.Point(x.ElapsedSeconds, UnitConversions.FromMetersPerSecond(x.SpeedMetersPerSecond!.Value, settings.SpeedUnit))).ToList();
            var bySecond = RideStatisticsCalculator.Resample(ride.Samples, s.ElapsedSeconds);
            var total = WorkoutTimeline?.TotalSeconds ?? 0;
            var overlay = new double?[Math.Max(total, 0)];
            foreach (var sec in bySecond)
            {
                if (sec.Second >= 0 && sec.Second < overlay.Length)
                {
                    overlay[sec.Second] = sec.Power;
                }
            }
            if (compliance?.PowerByWorkoutSecond is { Count: > 0 } exact)
            {
                // Recorded on the workout clock: exact even after pauses and skips.
                overlay = exact.Select(p => p >= 0 ? (double?)p : null).ToArray();
            }
            PowerBySecond = overlay;
            StatusText = "";
            SeriesLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load the ride: {ex.Message}";
            _services.Log.Error(DiagnosticCategory.App, $"Ride detail load failed: {ex}");
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (_ride is null || _services.Rides is null)
        {
            return;
        }
        var title = string.IsNullOrWhiteSpace(EditableTitle) ? null : EditableTitle.Trim();
        if (title is { Length: > 80 })
        {
            StatusText = "Keep the name under 80 characters.";
            return;
        }
        try
        {
            await _services.Rides.UpdateTitleAsync(_ride.Item.Id, title);
            Title = title ?? "Free ride";
            StatusText = "Renamed.";
            _ = _services.History.RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Rename failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ShowFiles() => HistoryViewModel.ShowFile(_services, _ride?.Item.FitPath, s => StatusText = s);

    public async Task DeleteAsync()
    {
        if (_ride is null || _services.Rides is null)
        {
            return;
        }
        try
        {
            await _services.Rides.DeleteAsync(_ride.Item.Id);
            _ = _services.History.RefreshAsync();
            Deleted?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
    }

    public string DeleteDescription => _ride is null ? "" : $"{Title}, {_ride.Item.StartedAt.ToLocalTime():d MMM yyyy h:mm tt}";
}
