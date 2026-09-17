using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KineticTrainer.App.Services;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Profiles;
using KineticTrainer.Core.Settings;
using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Units;
using KineticTrainer.Core.Zones;
using Microsoft.UI.Xaml.Media;

namespace KineticTrainer.App.ViewModels;

public sealed class ProfileItem(RiderProfile profile, bool isActive)
{
    public RiderProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Initials => Profile.Initials;
    public SolidColorBrush Brush { get; } = new(KineticColors.Hex(profile.Color));
    public string FtpText => Profile.Ftp is { } f ? $"FTP {f} W" + (Profile.FtpWattsPerKg is { } wkg ? $" · {wkg:0.00} W/kg" : "") : "FTP not set";
    public bool IsActive { get; } = isActive;
    public string ActiveText => IsActive ? "RIDING NOW" : "";
}

public sealed record BestRow(string Duration, string Watts, string WattsPerKg, string Date, long RideId);

public sealed record FtpRow(string Date, string Ftp, string Source);

public sealed record ColorOption(string Hex, SolidColorBrush Brush);

/// <summary>Local rider accounts: create, switch, edit, delete; records, zones and FTP history.</summary>
public sealed partial class ProfileViewModel : ObservableObject
{
    private const double KgPerLb = 0.45359237;
    private const double CmPerInch = 2.54;

    private readonly AppServices _services;

    public ProfileViewModel(AppServices services)
    {
        _services = services;
        services.RideSaved += x => _ = LoadDetailsAsync();
    }

    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<SummaryTile> RecordTiles { get; } = [];
    public ObservableCollection<BestRow> Bests { get; } = [];
    public ObservableCollection<ZoneRow> Zones { get; } = [];
    public ObservableCollection<FtpRow> FtpHistory { get; } = [];
    public IReadOnlyList<string> SexOptions { get; } = ["Not specified", "Female", "Male"];
    public IReadOnlyList<ColorOption> ColorOptions { get; } = RiderProfile.Palette.Select(h => new ColorOption(h, new SolidColorBrush(KineticColors.Hex(h)))).ToList();

    [ObservableProperty] private ProfileItem? _selected;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _heading = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double _ftp = double.NaN;
    [ObservableProperty] private double _weight = double.NaN;
    [ObservableProperty] private double _height = double.NaN;
    [ObservableProperty] private double _birthYear = double.NaN;
    [ObservableProperty] private int _sexIndex;
    [ObservableProperty] private double _maxHr = double.NaN;
    [ObservableProperty] private double _restHr = double.NaN;
    [ObservableProperty] private int _colorIndex;
    [ObservableProperty] private string _weightUnit = "lb";
    [ObservableProperty] private string _heightUnit = "in";
    [ObservableProperty] private string _derivedText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _canUse;
    [ObservableProperty] private bool _canDelete;
    [ObservableProperty] private string _maxPowerText = "none yet";
    [ObservableProperty] private string _maxPowerDate = "";
    [ObservableProperty] private string _ftpChartText = "";

    private bool Imperial => _services.Settings.DistanceUnit == DistanceUnit.Miles;

    /// <summary>FTP history for the chart (x = days since first entry).</summary>
    public event Action<IReadOnlyList<SeriesDownsampler.Point>>? FtpSeriesLoaded;

    public async Task LoadAsync()
    {
        WeightUnit = Imperial ? "lb" : "kg";
        HeightUnit = Imperial ? "in" : "cm";
        if (_services.Profiles is null)
        {
            Message = "Profiles are unavailable because the local database could not be opened.";
            return;
        }
        var keep = Selected?.Profile.Id ?? _services.ActiveProfile.Id;
        Profiles.Clear();
        foreach (var p in await _services.Profiles.ListAsync())
        {
            Profiles.Add(new ProfileItem(p, p.Id == _services.ActiveProfile.Id));
        }
        Selected = Profiles.FirstOrDefault(p => p.Profile.Id == keep) ?? Profiles.FirstOrDefault();
    }

    partial void OnSelectedChanged(ProfileItem? value)
    {
        if (value is null)
        {
            return;
        }
        IsNew = false;
        Fill(value.Profile);
        _ = LoadDetailsAsync();
    }

    private void Fill(RiderProfile p)
    {
        Heading = p.Name;
        Name = p.Name;
        Ftp = p.Ftp ?? double.NaN;
        Weight = p.WeightKg is { } kg ? Math.Round(Imperial ? kg / KgPerLb : kg, 1) : double.NaN;
        Height = p.HeightCm is { } cm ? Math.Round(Imperial ? cm / CmPerInch : cm, 1) : double.NaN;
        BirthYear = p.BirthYear ?? double.NaN;
        SexIndex = (int)p.Sex;
        MaxHr = p.MaxHeartRate ?? double.NaN;
        RestHr = p.RestingHeartRate ?? double.NaN;
        ColorIndex = Math.Max(0, RiderProfile.Palette.ToList().IndexOf(p.Color));
        CanUse = p.Id != _services.ActiveProfile.Id && p.Id != 0;
        CanDelete = p.Id != _services.ActiveProfile.Id && p.Id != 0;
        Message = "";
        UpdateDerived();
    }

    partial void OnFtpChanged(double value) => UpdateDerived();

    partial void OnWeightChanged(double value) => UpdateDerived();

    partial void OnHeightChanged(double value) => UpdateDerived();

    private void UpdateDerived()
    {
        var parts = new List<string>();
        var kg = ToKg(Weight);
        if (!double.IsNaN(Ftp) && kg is > 0)
        {
            parts.Add($"{Ftp / kg.Value:0.00} W/kg");
        }
        if (Imperial && !double.IsNaN(Height) && Height > 0)
        {
            parts.Add($"{(int)(Height / 12)} ft {Height % 12:0} in");
        }
        if (kg is > 0 && ToCm(Height) is { } cm && cm > 0)
        {
            parts.Add($"BMI {kg.Value / Math.Pow(cm / 100, 2):0.0}");
        }
        DerivedText = string.Join("   ·   ", parts);
    }

    private double? ToKg(double v) => double.IsNaN(v) ? null : Imperial ? v * KgPerLb : v;

    private double? ToCm(double v) => double.IsNaN(v) ? null : Imperial ? v * CmPerInch : v;

    private static int? ToInt(double v) => double.IsNaN(v) ? null : (int)Math.Round(v);

    private async Task LoadDetailsAsync()
    {
        var p = Selected?.Profile;
        RecordTiles.Clear();
        Bests.Clear();
        FtpHistory.Clear();
        Zones.Clear();
        MaxPowerText = "none yet";
        MaxPowerDate = "";
        if (p is null || p.Id == 0 || _services.Profiles is null)
        {
            return;
        }
        foreach (var z in ZoneRow.For(p.Ftp))
        {
            Zones.Add(z);
        }
        try
        {
            var r = await _services.Profiles.GetRecordsAsync(p.Id);
            var unit = _services.Settings.DistanceUnit;
            MaxPowerText = r.MaxPowerWatts is { } mp ? $"{mp} W" : "none yet";
            MaxPowerDate = r.MaxPowerDate is { } d ? $"set {d.ToLocalTime():d MMM yyyy}" : "no rides yet";
            RecordTiles.Add(new("RIDES", r.RideCount.ToString(), ""));
            RecordTiles.Add(new("TOTAL TIME", Format.Hours(r.TotalSeconds), ""));
            RecordTiles.Add(new("TOTAL DISTANCE", UnitConversions.FromMeters(r.TotalDistanceMeters, unit).ToString("0"), UnitConversions.Label(unit)));
            RecordTiles.Add(new("TOTAL WORK", r.TotalWorkKilojoules.ToString("0"), "kJ"));
            RecordTiles.Add(new("MAX CADENCE", Format.Number(r.MaxCadenceRpm), "rpm"));
            RecordTiles.Add(new("TOP SPEED", r.MaxSpeedMetersPerSecond is { } ms ? UnitConversions.FromMetersPerSecond(ms, _services.Settings.SpeedUnit).ToString("0.0") : "—",
                UnitConversions.Label(_services.Settings.SpeedUnit)));
            RecordTiles.Add(new("LONGEST RIDE", r.LongestRideSeconds is { } ls ? Format.Duration(ls) : "—", ""));
            RecordTiles.Add(new("FARTHEST", r.LongestRideMeters is { } lm ? UnitConversions.FromMeters(lm, unit).ToString("0.0") : "—", UnitConversions.Label(unit)));
            foreach (var b in r.PowerBests)
            {
                Bests.Add(new BestRow(PowerCurve.Label(b.DurationSeconds), $"{b.Watts:0} W",
                    p.WeightKg is > 0 ? $"{b.Watts / p.WeightKg.Value:0.00} W/kg" : "",
                    b.RideDate.ToLocalTime().ToString("d MMM yyyy"), b.RideId));
            }
            if (_services.FtpHistory is { } fh)
            {
                var entries = await fh.ListAsync(p.Id);
                foreach (var e in entries.Reverse())
                {
                    FtpHistory.Add(new FtpRow(e.EffectiveAt.ToLocalTime().ToString("d MMM yyyy"), $"{e.Ftp} W",
                        e.Source + (string.IsNullOrEmpty(e.Note) ? "" : $" ({e.Note})")));
                }
                if (entries.Count > 0)
                {
                    var t0 = entries[0].EffectiveAt;
                    var points = new List<SeriesDownsampler.Point>();
                    foreach (var e in entries)
                    {
                        var x = (e.EffectiveAt - t0).TotalSeconds;
                        if (points.Count > 0)
                        {
                            points.Add(new(x, points[^1].Y)); // step chart
                        }
                        points.Add(new(x, e.Ftp));
                    }
                    points.Add(new(Math.Max((DateTimeOffset.UtcNow - t0).TotalSeconds, points[^1].X + 1), entries[^1].Ftp));
                    FtpChartText = $"since {t0.ToLocalTime():d MMM yyyy}";
                    FtpSeriesLoaded?.Invoke(points);
                }
                else
                {
                    FtpChartText = "no FTP recorded yet";
                    FtpSeriesLoaded?.Invoke([]);
                }
            }
        }
        catch (Exception ex)
        {
            Message = $"Could not load records: {ex.Message}";
            _services.Log.Error(DiagnosticCategory.App, $"Profile records failed: {ex}");
        }
    }

    [RelayCommand]
    private void NewProfile()
    {
        Selected = null;
        IsNew = true;
        var color = RiderProfile.Palette[Profiles.Count % RiderProfile.Palette.Count];
        Fill(new RiderProfile { Name = $"Rider {Profiles.Count + 1}", Color = color });
        Heading = "New rider";
        CanUse = false;
        CanDelete = false;
        RecordTiles.Clear();
        Bests.Clear();
        FtpHistory.Clear();
        Zones.Clear();
        FtpSeriesLoaded?.Invoke([]);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var baseProfile = IsNew || Selected is null ? new RiderProfile { Name = Name } : Selected.Profile;
        var profile = baseProfile with
        {
            Name = (Name ?? "").Trim(),
            Ftp = ToInt(Ftp),
            WeightKg = ToKg(Weight) is { } kg ? Math.Round(kg, 2) : null,
            HeightCm = ToCm(Height) is { } cm ? Math.Round(cm, 1) : null,
            BirthYear = ToInt(BirthYear),
            Sex = (RiderSex)Math.Clamp(SexIndex, 0, 2),
            MaxHeartRate = ToInt(MaxHr),
            RestingHeartRate = ToInt(RestHr),
            Color = RiderProfile.Palette[Math.Clamp(ColorIndex, 0, RiderProfile.Palette.Count - 1)],
        };
        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            Message = string.Join(" ", errors);
            return;
        }
        try
        {
            var saved = await _services.SaveProfileAsync(profile);
            Message = IsNew ? $"Created {saved.Name}. Use this rider to ride as them." : "Saved.";
            IsNew = false;
            await LoadAsync();
            Selected = Profiles.FirstOrDefault(p => p.Profile.Id == saved.Id);
        }
        catch (Exception ex)
        {
            Message = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UseProfileAsync()
    {
        if (Selected is not { } item)
        {
            return;
        }
        try
        {
            await _services.SwitchProfileAsync(item.Profile.Id);
            await LoadAsync();
            Message = $"Now riding as {item.Name}.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { } item)
        {
            return;
        }
        try
        {
            await _services.DeleteProfileAsync(item.Profile.Id);
            Selected = null;
            await LoadAsync();
            Message = $"Deleted {item.Name} and all of their rides.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void TakeFtpTest()
    {
        _services.WorkoutsVm.ShowTests();
        _services.Navigate("workouts");
    }

    public void OpenRide(long rideId) => _services.Navigate("ride-detail", rideId);
}
