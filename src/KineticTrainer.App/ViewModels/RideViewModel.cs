using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KineticTrainer.App.Services;
using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Bluetooth.Calibration;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Profiles;
using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Core.Units;
using KineticTrainer.Core.Zones;
using KineticTrainer.Recording;
using KineticTrainer.Workouts.Library;
using KineticTrainer.Workouts.Model;
using KineticTrainer.Workouts.Playback;
using KineticTrainer.Workouts.Testing;
using KineticTrainer.Workouts.Timeline;
using Microsoft.UI.Xaml.Media;

namespace KineticTrainer.App.ViewModels;

/// <summary>A labelled number on the post-ride summary.</summary>
public sealed record SummaryTile(string Label, string Value, string Unit);

/// <summary>
/// The ride screen: live numbers, free rides and structured workouts, spin-down calibration prompts and the
/// post-ride summary. Values dim when there is no current sensor data; nothing is ever faked.
/// </summary>
public sealed partial class RideViewModel : ObservableObject
{
    private static readonly TimeSpan GraphSpanMax = TimeSpan.FromMinutes(20);

    private readonly AppServices _services;
    private readonly RollingWindow _power = new(GraphSpanMax);
    private TelemetryUiFeed? _feed;
    private DispatcherQueueTimerWrapper? _clock;
    private ExportResult? _lastExport;
    private TrainerTelemetry? _latest;
    private DateTimeOffset _lastPowerSample;
    private DateTimeOffset _lastGraphUpdate;
    private DateTimeOffset _popupUntil;
    private DateTimeOffset _cueTextUntil;
    private WorkoutEntry? _workout;
    private WorkoutPlayer? _player;
    private FtpEstimate? _pendingEstimate;
    private FtpTestKind _pendingEstimateKind;
    private int _rideMaxPower;

    // Live numbers
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string _powerZoneText = "";
    [ObservableProperty] private SolidColorBrush _powerZoneBrush = new(KineticColors.TextSecondary);
    [ObservableProperty] private string _percentFtpText = "";
    [ObservableProperty] private string _avg3sText = "—";
    [ObservableProperty] private string _avg10sText = "—";
    [ObservableProperty] private string _cadenceText = "—";
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _speedUnit = "mph";
    [ObservableProperty] private string _elapsedText = "0:00";
    [ObservableProperty] private string _distanceText = "0.00";
    [ObservableProperty] private string _distanceUnit = "mi";
    [ObservableProperty] private string _avgPowerText = "--";
    [ObservableProperty] private string _workText = "0";
    [ObservableProperty] private string _maxPowerText = "--";
    [ObservableProperty] private string _hintText = "Connect the trainer on the Sensor page.";
    [ObservableProperty] private double _valuesOpacity = 0.35;
    [ObservableProperty] private bool _isCoasting;
    [ObservableProperty] private string _riderText = "";

    // Ride
    [ObservableProperty] private bool _isRiding;
    [ObservableProperty] private string _rideButtonText = "Start ride";
    [ObservableProperty] private string _savedText = "";
    [ObservableProperty] private bool _hasSaved;
    [ObservableProperty] private int _graphMinutes = 5;

    // Calibration
    [ObservableProperty] private bool _isCalibrationOpen;
    [ObservableProperty] private bool _isCalibrating;
    [ObservableProperty] private string _calibrationText = "";
    [ObservableProperty] private string _calibrationDetail = "";
    [ObservableProperty] private string _calibrationSpeedText = "—";
    [ObservableProperty] private SolidColorBrush _calibrationBrush = new(KineticColors.Info);
    [ObservableProperty] private string _calibrationPreviousText = "";
    [ObservableProperty] private string _calibrationCloseText = "Cancel";
    [ObservableProperty] private bool _canCalibrate;

    // Workout
    [ObservableProperty] private bool _hasWorkout;
    [ObservableProperty] private bool _isWorkoutActive;
    [ObservableProperty] private bool _isWorkoutPaused;
    [ObservableProperty] private string _workoutName = "";
    [ObservableProperty] private string _workoutInfoText = "";
    [ObservableProperty] private string _segmentText = "";
    [ObservableProperty] private SolidColorBrush _segmentBrush = new(KineticColors.Accent);
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string _nextText = "";
    [ObservableProperty] private string _intervalRemainingText = "";
    [ObservableProperty] private string _workoutElapsedText = "";
    [ObservableProperty] private string _workoutRemainingText = "";
    [ObservableProperty] private string _targetStateText = "";
    [ObservableProperty] private SolidColorBrush _targetStateBrush = new(KineticColors.Surface);
    [ObservableProperty] private string _intensityText = "100%";
    [ObservableProperty] private bool _canEndRamp;
    [ObservableProperty] private string _pauseButtonText = "Pause";
    [ObservableProperty] private string _workoutButtonText = "Start workout";
    [ObservableProperty] private string _cueText = "";
    [ObservableProperty] private string _ftpNoteText = "";
    [ObservableProperty] private double _workoutElapsed = -1;
    [ObservableProperty] private WorkoutTimeline? _timeline;
    [ObservableProperty] private int _chartFtp;
    [ObservableProperty] private bool _colorByZone = true;
    [ObservableProperty] private bool _voiceOn = true;
    [ObservableProperty] private string _voiceButtonText = "Voice on";
    [ObservableProperty] private string _voiceGlyph = "\uE767";

    // Interval pop-up
    [ObservableProperty] private bool _isPopupVisible;
    [ObservableProperty] private string _popupTitle = "";
    [ObservableProperty] private string _popupDuration = "";
    [ObservableProperty] private string _popupTarget = "";

    // Summary
    [ObservableProperty] private bool _isSummaryVisible;
    [ObservableProperty] private string _summaryTitle = "";
    [ObservableProperty] private string _summarySubtitle = "";
    [ObservableProperty] private string _summaryRecordsText = "";
    [ObservableProperty] private bool _hasFtpResult;
    [ObservableProperty] private string _ftpResultText = "";
    [ObservableProperty] private string _ftpResultDetail = "";
    [ObservableProperty] private bool _canSaveFtp;

    public RideViewModel(AppServices services)
    {
        _services = services;
    }

    public ObservableCollection<SummaryTile> SummaryTiles { get; } = [];

    public IReadOnlyList<string> GraphChoiceLabels { get; } = Core.Settings.AppSettings.PowerGraphChoices.Select(m => $"{m} min").ToList();

    public int GraphIndex
    {
        get => Math.Max(0, Core.Settings.AppSettings.PowerGraphChoices.ToList().IndexOf(GraphMinutes));
        set
        {
            if (value >= 0 && value < Core.Settings.AppSettings.PowerGraphChoices.Count)
            {
                GraphMinutes = Core.Settings.AppSettings.PowerGraphChoices[value];
            }
        }
    }

    /// <summary>Raised about once a second with the rolling power graph (x = seconds before now).</summary>
    public event Action<IReadOnlyList<SeriesDownsampler.Point>, double?>? LiveGraphUpdated;

    /// <summary>Raised about once a second while a workout plays, with power per workout second.</summary>
    public event Action<double?[]>? WorkoutPowerUpdated;

    public void Start()
    {
        ApplySettings(_services.Settings);
        ApplyProfile(_services.ActiveProfile);
        _services.SettingsChanged += s => _services.Dispatcher.TryEnqueue(() => ApplySettings(s));
        _services.ActiveProfileChanged += p => ApplyProfile(p);
        _services.Session.StateChanged += _ => _services.Dispatcher.TryEnqueue(UpdateCanCalibrate);
        _services.Session.CalibrationChanged += snap => _services.Dispatcher.TryEnqueue(() => ApplyCalibration(snap));
        _feed = new TelemetryUiFeed(_services.Telemetry, _services.Dispatcher, "ride", Apply);
        _feed.Start();
        _clock = new DispatcherQueueTimerWrapper(_services.Dispatcher, TimeSpan.FromMilliseconds(250), Tick);
        _clock.Start();
        UpdateCanCalibrate();
    }

    private void ApplySettings(Core.Settings.AppSettings s)
    {
        SpeedUnit = UnitConversions.Label(s.SpeedUnit);
        DistanceUnit = UnitConversions.Label(s.DistanceUnit);
        GraphMinutes = s.PowerGraphMinutes;
        ColorByZone = s.ColorProfilesByZone;
        VoiceOn = s.SpokenCuesEnabled;
        UpdateRideClock();
    }

    private void ApplyProfile(RiderProfile p)
    {
        RiderText = p.Ftp is { } f ? $"{p.Name}  ·  FTP {f} W" : $"{p.Name}  ·  FTP not set";
        if (_player is null)
        {
            ChartFtp = _services.EffectiveFtp;
            UpdateFtpNote();
        }
    }

    private void UpdateFtpNote()
    {
        FtpNoteText = _services.HasFtp || !HasWorkout
            ? ""
            : $"No FTP set for {_services.ActiveProfile.Name}: targets use {AppServices.FallbackFtp} W. Set your FTP on the Profile page or take an FTP test.";
    }

    partial void OnVoiceOnChanged(bool value)
    {
        VoiceButtonText = value ? "Voice on" : "Voice off";
        VoiceGlyph = value ? "\uE767" : "\uE74F";
        if (value != _services.Settings.SpokenCuesEnabled)
        {
            _ = _services.UpdateSettingsAsync(_services.Settings with { SpokenCuesEnabled = value });
            _services.Log.Info(DiagnosticCategory.App, value ? "Spoken cues turned on" : "Spoken cues turned off");
        }
    }

    /// <summary>Mutes or unmutes the workout voice (tones are controlled in Settings).</summary>
    [RelayCommand]
    private void ToggleVoice() => VoiceOn = !VoiceOn;

    partial void OnGraphMinutesChanged(int value)
    {
        if (value != _services.Settings.PowerGraphMinutes && Core.Settings.AppSettings.PowerGraphChoices.Contains(value))
        {
            _ = _services.UpdateSettingsAsync(_services.Settings with { PowerGraphMinutes = value });
        }
        _lastGraphUpdate = default;
        OnPropertyChanged(nameof(GraphIndex));
    }

    // ---------------------------------------------------------------- telemetry

    private void Apply(TrainerTelemetry t)
    {
        _latest = t;
        if (!t.IsDataValid)
        {
            ValuesOpacity = 0.35;
            IsCoasting = false;
            HintText = t.ConnectionState switch
            {
                TrainerConnectionState.Connected => "Waiting for data from the sensor…",
                TrainerConnectionState.Reconnecting => IsRiding ? "Sensor lost. Reconnecting… (the ride timer keeps running)" : "Sensor lost. Reconnecting…",
                TrainerConnectionState.Searching => "Looking for the remembered sensor… spin the roller to wake it",
                TrainerConnectionState.Connecting => "Connecting…",
                _ => "Connect the trainer on the Sensor page.",
            };
            return;
        }

        ValuesOpacity = 1.0;
        if (t.PowerWatts is { } p && t.Timestamp != _lastPowerSample)
        {
            _lastPowerSample = t.Timestamp;
            _power.Add(t.Timestamp, Math.Max(0, p));
            if (_services.Recorder.IsRecording && p > _rideMaxPower)
            {
                _rideMaxPower = p;
            }
        }
        PowerText = t.PowerWatts is { } pw ? Math.Max(0, pw).ToString() : "—";
        var ftp = _services.ActiveProfile.Ftp;
        if (PowerZones.ZoneFor(t.PowerWatts, ftp) is { } zone)
        {
            PowerZoneText = zone.Label.ToUpperInvariant();
            PowerZoneBrush = KineticColors.ZoneBrush(zone.Number);
            PercentFtpText = $"{PowerZones.PercentOfFtp(t.PowerWatts, ftp):0}% FTP";
        }
        else
        {
            PowerZoneText = ftp is null ? "SET FTP FOR ZONES" : "";
            PowerZoneBrush = new SolidColorBrush(KineticColors.TextSecondary);
            PercentFtpText = "";
        }
        CadenceText = t.CadenceRpm is { } c ? Math.Round(c).ToString("0") : "—";
        var unit = _services.Settings.SpeedUnit;
        SpeedText = t.SpeedMetersPerSecond is { } s ? UnitConversions.FromMetersPerSecond(s, unit).ToString("0.0") : "—";
        IsCoasting = t.Coasting == true;
        HintText = t.IsSimulated ? "SIMULATED SENSOR: not real ride data" : "";
        if (IsCalibrationOpen)
        {
            CalibrationSpeedText = SpeedText;
        }
    }

    private bool HasFreshData(DateTimeOffset now) =>
        _latest is { IsDataValid: true } l && now - l.Timestamp < TimeSpan.FromSeconds(5);

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = HasFreshData(now);
        var a3 = fresh ? _power.Average(TimeSpan.FromSeconds(3), now) : null;
        var a10 = fresh ? _power.Average(TimeSpan.FromSeconds(10), now) : null;
        Avg3sText = a3 is { } x3 ? x3.ToString("0") : "—";
        Avg10sText = a10 is { } x10 ? x10.ToString("0") : "—";
        UpdateRideClock();

        if (_player is { } player)
        {
            var power = fresh ? _latest!.PowerWatts : null;
            var snap = player.Update(now, new PlayerInput(power is { } p ? Math.Max(0, p) : null, a3, fresh ? _latest!.CadenceRpm : null));
            ApplySnapshot(snap);
        }
        if (now >= _popupUntil && IsPopupVisible)
        {
            IsPopupVisible = false;
        }
        if (now >= _cueTextUntil && CueText.Length > 0)
        {
            CueText = "";
        }

        if (now - _lastGraphUpdate >= TimeSpan.FromSeconds(1))
        {
            _lastGraphUpdate = now;
            if (_player is not null)
            {
                WorkoutPowerUpdated?.Invoke(_player.PowerBySecond());
            }
            else
            {
                var span = TimeSpan.FromMinutes(GraphMinutes);
                var points = _power.Snapshot(now).Where(pt => pt.X >= -span.TotalSeconds).ToList();
                LiveGraphUpdated?.Invoke(points, _services.ActiveProfile.Ftp);
            }
        }
    }

    private void UpdateRideClock()
    {
        var r = _services.Recorder;
        if (r.IsRecording)
        {
            ElapsedText = Format.Duration(r.Elapsed.TotalSeconds);
            DistanceText = UnitConversions.FromMeters(r.DistanceMeters, _services.Settings.DistanceUnit).ToString("0.00");
            AvgPowerText = r.AveragePowerWatts is { } p ? p.ToString("0") : "--";
            MaxPowerText = _rideMaxPower > 0 ? _rideMaxPower.ToString() : "--";
            var samples = r.SampleCount;
            WorkText = samples > 0 && r.AveragePowerWatts is { } ap ? (ap * r.Elapsed.TotalSeconds / 1000).ToString("0") : "0";
        }
    }

    // ---------------------------------------------------------------- free ride

    [RelayCommand]
    private async Task ToggleRideAsync()
    {
        if (!_services.Recorder.IsRecording)
        {
            StartRecording();
            return;
        }
        await StopAndSaveAsync();
    }

    private void StartRecording()
    {
        _services.Recorder.Start(ftp: _services.ActiveProfile.Ftp);
        IsRiding = true;
        RideButtonText = "Stop ride";
        SavedText = "";
        HasSaved = false;
        IsSummaryVisible = false;
        ElapsedText = "0:00";
        DistanceText = "0.00";
        AvgPowerText = "--";
        MaxPowerText = "--";
        _rideMaxPower = 0;
        WorkText = "0";
        _services.Log.Info(DiagnosticCategory.App, $"Ride started for {_services.ActiveProfile.Name}");
    }

    /// <summary>Called when the app closes: a ride in progress is saved rather than lost.</summary>
    public async Task SaveUnfinishedRideAsync()
    {
        if (_services.Recorder.IsRecording)
        {
            _services.Log.Info(DiagnosticCategory.App, "App closing during a ride: saving it");
            await StopAndSaveAsync();
        }
    }

    private async Task StopAndSaveAsync()
    {
        var now = DateTimeOffset.UtcNow;
        WorkoutCompliance? compliance = null;
        FtpEstimate? estimate = null;
        var workout = _player is not null ? _workout : null;
        if (_player is { } player)
        {
            player.Stop(now);
            estimate = player.EstimateFtp();
            compliance = player.BuildCompliance(estimate);
            EndWorkoutPlayback();
        }

        var ride = _services.Recorder.Stop(now);
        IsRiding = false;
        RideButtonText = "Start ride";
        if (ride is null)
        {
            return;
        }
        if (workout is not null)
        {
            ride = ride with
            {
                Title = workout.Name,
                WorkoutZwo = workout.Zwo,
                ComplianceJson = compliance?.ToJson(),
            };
        }
        var s = ride.Summary;
        _services.Log.Info(DiagnosticCategory.App,
            $"Ride stopped: {s.ElapsedSeconds:0}s, {s.DistanceMeters:0} m, {s.SampleCount} samples, avg {s.AveragePowerWatts:0} W, max {s.MaxPowerWatts} W, NP {s.NormalizedPowerWatts:0} W, " +
            $"IF {s.IntensityFactor:0.00}, TSS {s.TrainingStressScore:0}");
        if (ride.Samples.Count == 0)
        {
            SavedText = "Ride not saved: no sensor data was received.";
            HasSaved = false;
            return;
        }

        string? fitPath = null, tcxPath = null;
        try
        {
            _lastExport = await RideExporter.ExportAsync(ride, _services.ExportFolder);
            fitPath = _lastExport.FitPath;
            tcxPath = _lastExport.TcxPath;
            SavedText = $"Saved {Path.GetFileName(_lastExport.FitPath)} and .tcx";
            HasSaved = true;
            _services.Log.Info(DiagnosticCategory.App, $"Exported {_lastExport.FitPath} and {_lastExport.TcxPath}");
        }
        catch (Exception ex)
        {
            SavedText = $"Ride finished but export failed: {ex.Message}";
            HasSaved = false;
            _services.Log.Error(DiagnosticCategory.App, $"Export failed: {ex}");
        }

        // History is independent of the files: a database problem must not lose the export, and vice versa.
        var records = new List<string>();
        if (_services.Rides is { } rides && _services.Profiles is { } profiles && _services.ActiveProfile.Id > 0)
        {
            try
            {
                var before = await profiles.GetRecordsAsync(_services.ActiveProfile.Id);
                var id = await rides.SaveAsync(_services.ActiveProfile.Id, ride, _services.Session.Protocol?.ToString(), fitPath, tcxPath);
                _services.Log.Info(DiagnosticCategory.App, $"Ride saved to history as #{id}");
                if (!ride.Simulated)
                {
                    records = NewRecords(before, ride);
                }
                _services.NotifyRideSaved(id);
            }
            catch (Exception ex)
            {
                SavedText += "  (not added to history: " + ex.Message + ")";
                _services.Log.Error(DiagnosticCategory.App, $"Saving ride to history failed: {ex}");
            }
        }
        ShowSummary(ride, compliance, estimate, records);
    }

    private static List<string> NewRecords(ProfileRecords before, RideRecording ride)
    {
        var list = new List<string>();
        var s = ride.Summary;
        if (s.MaxPowerWatts is { } max && (before.MaxPowerWatts is null || max > before.MaxPowerWatts))
        {
            list.Add($"max power {max} W");
        }
        if (s.PowerBests is { } bests)
        {
            foreach (var d in new[] { 5, 60, 300, 1200, 3600 })
            {
                if (bests.TryGetValue(d, out var w))
                {
                    var old = before.PowerBests.FirstOrDefault(b => b.DurationSeconds == d);
                    if (old is null || w > old.Watts + 0.5)
                    {
                        list.Add($"{PowerCurve.Label(d)} {w:0} W");
                    }
                }
            }
        }
        return list;
    }

    private void ShowSummary(RideRecording ride, WorkoutCompliance? compliance, FtpEstimate? estimate, List<string> records)
    {
        var s = ride.Summary;
        var settings = _services.Settings;
        SummaryTitle = ride.Title ?? "Free ride";
        SummarySubtitle = $"{ride.StartedAt.ToLocalTime():dddd d MMMM, HH:mm}  ·  {_services.ActiveProfile.Name}" + (ride.Simulated ? "  ·  SIMULATED" : "");
        SummaryTiles.Clear();
        SummaryTiles.Add(new("TIME", Format.Duration(s.ElapsedSeconds), ""));
        SummaryTiles.Add(new("DISTANCE", UnitConversions.FromMeters(s.DistanceMeters, settings.DistanceUnit).ToString("0.00"), UnitConversions.Label(settings.DistanceUnit)));
        SummaryTiles.Add(new("AVG POWER", s.AveragePowerWatts?.ToString("0") ?? "—", "W"));
        SummaryTiles.Add(new("NORMALIZED", s.NormalizedPowerWatts?.ToString("0") ?? "—", "W"));
        SummaryTiles.Add(new("MAX POWER", s.MaxPowerWatts?.ToString() ?? "—", "W"));
        SummaryTiles.Add(new("WORK", s.WorkKilojoules?.ToString("0") ?? "—", "kJ"));
        SummaryTiles.Add(new("IF", s.IntensityFactor?.ToString("0.00") ?? "—", ""));
        SummaryTiles.Add(new("TSS", s.TrainingStressScore?.ToString("0") ?? "—", ""));
        SummaryTiles.Add(new("AVG CADENCE", s.AverageCadenceRpm?.ToString("0") ?? "—", "rpm"));
        if (compliance?.OnTargetPercent is { } on)
        {
            SummaryTiles.Add(new("ON TARGET", on.ToString("0"), "%"));
        }
        if (s.Ftp is null)
        {
            SummaryTiles.Add(new("NOTE", "No FTP", "IF/TSS need an FTP"));
        }
        SummaryRecordsText = records.Count == 0 ? "" : "New personal records: " + string.Join(",  ", records);

        _pendingEstimate = estimate;
        _pendingEstimateKind = estimate?.Test ?? FtpTestKind.None;
        HasFtpResult = estimate is not null;
        if (estimate is { } e)
        {
            if (e.Ftp is { } ftp)
            {
                var current = _services.ActiveProfile.Ftp;
                FtpResultText = $"Estimated FTP: {ftp} W";
                FtpResultDetail = $"{e.Method} ({e.BasisWatts:0} W × {e.Factor:0.00})." +
                                  (current is { } c ? $" Your current FTP is {c} W." : "") +
                                  (e.Warnings.Count > 0 ? "\n" + string.Join("\n", e.Warnings) : "") +
                                  (ride.Simulated ? "\nThis was ridden on the simulated sensor." : "");
                CanSaveFtp = current != ftp && _services.Profiles is not null;
            }
            else
            {
                FtpResultText = "No FTP estimate";
                FtpResultDetail = string.Join("\n", e.Warnings);
                CanSaveFtp = false;
            }
        }
        IsSummaryVisible = true;
    }

    [RelayCommand]
    private async Task SaveFtpAsync()
    {
        if (_pendingEstimate?.Ftp is not { } ftp)
        {
            return;
        }
        try
        {
            var testName = _pendingEstimateKind switch
            {
                FtpTestKind.TwentyMinute => "20-minute test",
                FtpTestKind.Ramp => "ramp test",
                FtpTestKind.EightMinute => "8-minute test",
                _ => "FTP test",
            };
            await _services.SaveProfileAsync(_services.ActiveProfile with { Ftp = ftp }, "ftp test", testName);
            CanSaveFtp = false;
            FtpResultDetail = $"Saved. {_services.ActiveProfile.Name}'s FTP is now {ftp} W; zones and targets use it from now on.";
            _services.Log.Info(DiagnosticCategory.App, $"FTP set to {ftp} W from a {testName}");
        }
        catch (Exception ex)
        {
            FtpResultDetail = $"Could not save the FTP: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DismissSummary() => IsSummaryVisible = false;

    [RelayCommand]
    private void OpenHistory()
    {
        IsSummaryVisible = false;
        _services.Navigate("history");
    }

    // ---------------------------------------------------------------- workouts

    /// <summary>Selects a workout to ride (from the Workouts page).</summary>
    public bool LoadWorkout(WorkoutEntry entry)
    {
        if (_player is not null)
        {
            CueText = "Finish or stop the current workout before choosing another.";
            _cueTextUntil = DateTimeOffset.UtcNow.AddSeconds(8);
            return false;
        }
        _workout = entry;
        HasWorkout = true;
        IsWorkoutActive = false;
        WorkoutName = entry.Name;
        var m = entry.Metrics;
        WorkoutInfoText = $"{Format.Duration(entry.Timeline.TotalSeconds)}  ·  IF {m.IntensityFactor:0.00}  ·  TSS {m.TrainingStressScore:0}" +
                          (entry.Workout.FtpTest != FtpTestKind.None ? "  ·  FTP TEST" : "");
        WorkoutButtonText = IsRiding ? "Start workout now" : "Start workout";
        ChartFtp = _services.EffectiveFtp;
        WorkoutElapsed = -1;
        Timeline = entry.Timeline;
        var first = entry.Timeline.Segments.FirstOrDefault();
        SegmentText = first is null ? "" : $"FIRST: {first.Label.ToUpperInvariant()}";
        TargetText = first is null ? "" : entry.Timeline.DescribeTarget(first, _services.EffectiveFtp);
        NextText = "";
        IntervalRemainingText = first is null ? "" : Format.Duration(first.DurationSeconds);
        WorkoutElapsedText = "0:00";
        WorkoutRemainingText = Format.Duration(entry.Timeline.TotalSeconds);
        TargetStateText = "";
        TargetStateBrush = new SolidColorBrush(KineticColors.Surface);
        UpdateFtpNote();
        _services.Log.Info(DiagnosticCategory.App, $"Workout selected: {entry.Name}");
        return true;
    }

    [RelayCommand]
    private void StartWorkout()
    {
        if (_workout is not { } entry || _player is not null)
        {
            return;
        }
        var options = new PlayerOptions { TolerancePercent = _services.Settings.TargetTolerancePercent };
        var player = new WorkoutPlayer(entry.Workout, _services.EffectiveFtp, options);
        player.CueRaised += OnCue;
        _player = player;
        if (!_services.Recorder.IsRecording)
        {
            StartRecording();
        }
        IsWorkoutActive = true;
        IsWorkoutPaused = false;
        PauseButtonText = "Pause";
        ChartFtp = _services.EffectiveFtp;
        ApplySnapshot(player.Start(DateTimeOffset.UtcNow));
        _services.Log.Info(DiagnosticCategory.App, $"Workout started: {entry.Name} at FTP {_services.EffectiveFtp} W" + (_services.HasFtp ? "" : " (fallback)"));
    }

    [RelayCommand]
    private void PauseResumeWorkout()
    {
        if (_player is not { } p)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        if (p.State == PlayerState.Paused)
        {
            p.Resume(now);
            PauseButtonText = "Pause";
            IsWorkoutPaused = false;
        }
        else if (p.State == PlayerState.Running)
        {
            p.Pause(now);
            PauseButtonText = "Resume";
            IsWorkoutPaused = true;
        }
        ApplySnapshot(p.Snapshot(null));
    }

    [RelayCommand]
    private void SkipSegment()
    {
        if (_player is { } p)
        {
            p.Skip(DateTimeOffset.UtcNow);
            ApplySnapshot(p.Snapshot(null));
        }
    }

    [RelayCommand]
    private void EndRamp()
    {
        if (_player is { } p)
        {
            p.EndRamp(DateTimeOffset.UtcNow);
            ApplySnapshot(p.Snapshot(null));
        }
    }

    [RelayCommand]
    private void IntensityUp() => ChangeIntensity(+5);

    [RelayCommand]
    private void IntensityDown() => ChangeIntensity(-5);

    private void ChangeIntensity(int delta)
    {
        if (_player is { } p)
        {
            p.SetIntensity(p.IntensityPercent + delta);
            IntensityText = $"{p.IntensityPercent}%";
            ApplySnapshot(p.Snapshot(null));
        }
    }

    /// <summary>Stops following the workout; the ride keeps recording as a free ride.</summary>
    [RelayCommand]
    private void EndWorkout()
    {
        if (_player is { } p)
        {
            p.Stop(DateTimeOffset.UtcNow);
            _services.Log.Info(DiagnosticCategory.App, "Workout ended by the rider; the ride continues");
        }
        EndWorkoutPlayback();
        ClearWorkout();
    }

    [RelayCommand]
    private void ClearWorkout()
    {
        if (_player is not null)
        {
            return;
        }
        _workout = null;
        HasWorkout = false;
        Timeline = null;
        WorkoutElapsed = -1;
        FtpNoteText = "";
        TargetStateBrush = new SolidColorBrush(KineticColors.Surface);
        TargetStateText = "";
    }

    [RelayCommand]
    private void ChooseWorkout() => _services.Navigate("workouts");

    private void EndWorkoutPlayback()
    {
        if (_player is { } p)
        {
            p.CueRaised -= OnCue;
        }
        _player = null;
        IsWorkoutActive = false;
        IsWorkoutPaused = false;
        CanEndRamp = false;
        IsPopupVisible = false;
        IntensityText = "100%";
        WorkoutButtonText = "Start workout";
    }

    private void OnCue(WorkoutCue cue)
    {
        _services.Cues.Play(cue);
        var now = DateTimeOffset.UtcNow;
        if (cue.Kind is CueKind.Text or CueKind.Upcoming or CueKind.Complete)
        {
            CueText = cue.Message;
            _cueTextUntil = now.AddSeconds(cue.Kind == CueKind.Complete ? 3600 : 8);
        }
        if (cue.Kind is CueKind.SegmentStart or CueKind.Recovery or CueKind.Cooldown && cue.SegmentIndex is { } i && _player is { } p
            && _services.Settings.ShowIntervalPopups)
        {
            var seg = p.Timeline.Segments[i];
            if (seg.DurationSeconds >= 15 && i > 0)
            {
                PopupTitle = seg.Label;
                PopupDuration = Format.Duration(seg.DurationSeconds);
                PopupTarget = p.Timeline.DescribeTarget(seg, p.Ftp, p.IntensityPercent / 100.0);
                IsPopupVisible = true;
                _popupUntil = now.AddSeconds(3);
            }
        }
        if (cue.Kind == CueKind.Complete && _player is { } done)
        {
            _services.Log.Info(DiagnosticCategory.App, $"Workout complete: {done.Workout.Name}");
            var estimate = done.EstimateFtp();
            CueText = estimate?.Ftp is { } ftp
                ? $"Workout complete. Estimated FTP {ftp} W. Stop the ride to save it."
                : "Workout complete. Keep spinning, and stop the ride to save it.";
        }
    }

    private void ApplySnapshot(PlayerSnapshot s)
    {
        WorkoutElapsed = s.ElapsedSeconds;
        WorkoutElapsedText = Format.Duration(s.ElapsedSeconds);
        WorkoutRemainingText = Format.Duration(s.TotalRemainingSeconds);
        CanEndRamp = s.CanEndRamp;
        IntensityText = $"{s.IntensityPercent}%";
        if (s.State == PlayerState.Completed)
        {
            SegmentText = "WORKOUT COMPLETE";
            SegmentBrush = new SolidColorBrush(KineticColors.Accent);
            TargetText = "Free ride";
            NextText = "";
            IntervalRemainingText = "0:00";
            TargetStateText = "";
            TargetStateBrush = new SolidColorBrush(KineticColors.Surface);
            return;
        }
        if (s.Segment is not { } seg)
        {
            return;
        }
        var fraction = _player?.Timeline.ToFraction(seg.PowerAt(s.ElapsedSeconds - seg.StartSecond), _player.Ftp);
        var zone = fraction is { } f ? PowerZones.ZoneForFraction(f) : null;
        SegmentText = (s.State == PlayerState.Paused ? "PAUSED  ·  " : "") + seg.Label.ToUpperInvariant() +
                      (zone is null ? "" : $"  ·  {zone.Label.ToUpperInvariant()}");
        SegmentBrush = zone is null ? new SolidColorBrush(KineticColors.TextSecondary) : KineticColors.ZoneBrush(zone.Number);
        TargetText = s.TargetWatts is { } tw && seg.Role != SegmentRole.MaxEffort && (fraction ?? 0) < WorkoutTimeline.MaxDisplayThreshold
            ? $"{tw} W"
            : s.TargetText;
        IntervalRemainingText = Format.Duration(Math.Ceiling(s.SegmentRemainingSeconds));
        NextText = s.NextSegment is { } next ? $"Next: {next.Label}, {s.NextTargetText} for {Format.Duration(next.DurationSeconds)}" : "Last segment";
        (TargetStateText, TargetStateBrush) = s.TargetState switch
        {
            TargetState.OnTarget => ("ON TARGET", new SolidColorBrush(KineticColors.Accent)),
            TargetState.Under => ($"PUSH  +{-(s.DeviationWatts ?? 0):0} W", new SolidColorBrush(KineticColors.Info)),
            TargetState.Over => ($"EASE OFF  −{s.DeviationWatts ?? 0:0} W", new SolidColorBrush(KineticColors.Danger)),
            TargetState.NoData => ("NO POWER DATA", new SolidColorBrush(KineticColors.TextSecondary)),
            _ => (seg.Role == SegmentRole.MaxEffort ? "ALL OUT!" : "", new SolidColorBrush(KineticColors.Surface)),
        };
    }

    // ---------------------------------------------------------------- calibration

    private void UpdateCanCalibrate()
    {
        CanCalibrate = _services.Session.State == TrainerConnectionState.Connected && _services.Session.Protocol == TrainerProtocolKind.FitnessMachine;
        if (!CanCalibrate && IsCalibrating)
        {
            IsCalibrating = false;
            CalibrationCloseText = "Close";
        }
    }

    private void ApplyCalibration(CalibrationSnapshot snap)
    {
        IsCalibrating = snap.IsActive;
        if (snap.IsActive)
        {
            IsCalibrationOpen = true;
        }
        CalibrationText = snap.Instruction;
        CalibrationDetail = snap.Detail;
        CalibrationSpeedText = SpeedText;
        CalibrationBrush = new SolidColorBrush(snap.Phase switch
        {
            CalibrationPhase.Coast or CalibrationPhase.Succeeded => KineticColors.Accent,
            CalibrationPhase.Failed or CalibrationPhase.Cancelled => KineticColors.Danger,
            _ => KineticColors.Info,
        });
        CalibrationCloseText = snap.IsActive ? "Cancel" : "Close";
        if (!snap.IsActive && snap.Phase != CalibrationPhase.Idle)
        {
            if (snap.Phase is CalibrationPhase.Succeeded or CalibrationPhase.Failed && _services.Rides is { } rides)
            {
                var record = new Persistence.CalibrationRecord(0, DateTimeOffset.UtcNow, _services.Session.DeviceInfo?.Name,
                    _services.Session.Protocol?.ToString(), snap.Phase == CalibrationPhase.Succeeded, snap.Result, snap.TargetLowMps, snap.TargetHighMps);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await rides.RecordCalibrationAsync(record);
                    }
                    catch (Exception ex)
                    {
                        _services.Log.Warn(DiagnosticCategory.App, $"Calibration not recorded: {ex.Message}");
                    }
                });
            }
        }
    }

    [RelayCommand]
    private async Task StartCalibrationAsync()
    {
        var session = _services.Session;
        if (session.Calibration is { IsActive: true })
        {
            IsCalibrationOpen = true;
            return;
        }
        CalibrationPreviousText = await PreviousCalibrationTextAsync();
        try
        {
            var unit = _services.Settings.SpeedUnit;
            IsCalibrationOpen = true;
            session.StartCalibration(mps => $"{UnitConversions.FromMetersPerSecond(mps, unit):0} {UnitConversions.Label(unit)}");
        }
        catch (Exception ex)
        {
            CalibrationText = "Cannot calibrate";
            CalibrationDetail = ex.Message;
            CalibrationCloseText = "Close";
        }
    }

    [RelayCommand]
    private void CloseCalibration()
    {
        if (_services.Session.Calibration is { IsActive: true })
        {
            _services.Session.CancelCalibration();
        }
        IsCalibrationOpen = false;
    }

    private async Task<string> PreviousCalibrationTextAsync()
    {
        if (_services.Rides is not { } rides)
        {
            return "";
        }
        try
        {
            var last = (await rides.ListCalibrationsAsync(10)).FirstOrDefault(c => c.Succeeded);
            return last is null ? "Previous calibration: none recorded" : $"Previous calibration: {last.PerformedAt.ToLocalTime():d MMM yyyy}, {last.Result}";
        }
        catch
        {
            return "";
        }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        try
        {
            var folder = _services.ExportFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", _lastExport is { } e ? $"/select,\"{e.FitPath}\"" : folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SavedText = ex.Message;
        }
    }
}
