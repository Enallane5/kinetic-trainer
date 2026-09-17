using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KineticTrainer.App.Controls;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Units;

namespace KineticTrainer.App.ViewModels;

/// <summary>Weekly/monthly volume, training load (TSS) and fitness/fatigue for the active rider.</summary>
public sealed partial class TrendsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public TrendsViewModel(AppServices services)
    {
        _services = services;
        services.ActiveProfileChanged += x => _ = LoadAsync();
        services.RideSaved += x => _ = LoadAsync();
    }

    public IReadOnlyList<string> RangeOptions { get; } = ["Last 12 weeks", "Last 26 weeks", "Last 12 months"];
    public ObservableCollection<SummaryTile> Tiles { get; } = [];

    [ObservableProperty] private int _rangeIndex;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _riderName = "";
    [ObservableProperty] private string _loadNote = "";

    public event Action<IReadOnlyList<Bar>, IReadOnlyList<Bar>, IReadOnlyList<Bar>, string>? ChartsLoaded;

    public event Action<IReadOnlyList<SeriesDownsampler.Point>, IReadOnlyList<SeriesDownsampler.Point>>? LoadCurveLoaded;

    partial void OnRangeIndexChanged(int value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        RiderName = _services.ActiveProfile.Name;
        if (_services.Rides is null || _services.ActiveProfile.Id == 0)
        {
            StatusText = "Trends are unavailable because the local database could not be opened.";
            return;
        }
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var monthly = RangeIndex == 2;
            var periods = RangeIndex switch { 1 => 26, 2 => 12, _ => 12 };
            var from = monthly ? TrainingLoad.MonthStart(today).AddMonths(-(periods - 1)) : TrainingLoad.WeekStart(today).AddDays(-7 * (periods - 1));
            // Load 6 extra weeks so fitness (42-day average) is seeded.
            var loadFrom = from.AddDays(-42);
            var rides = await _services.Rides.ListBetweenAsync(_services.ActiveProfile.Id,
                new DateTimeOffset(loadFrom.ToDateTime(TimeOnly.MinValue)), DateTimeOffset.Now.AddDays(1));
            var entries = rides.Where(r => !r.Simulated)
                .Select(r => new LoadEntry(r.StartedAt, r.ElapsedSeconds, r.DistanceMeters, r.TrainingStressScore, r.WorkKilojoules))
                .ToList();

            var totals = monthly ? TrainingLoad.Monthly(entries, today, periods) : TrainingLoad.Weekly(entries, today, periods);
            var unit = _services.Settings.DistanceUnit;
            string Label(DateOnly d) => monthly ? d.ToString("MMM") : d.ToString("d MMM");
            var hours = totals.Select(t => new Bar(Label(t.PeriodStart), t.Seconds / 3600, (t.Seconds / 3600).ToString("0.#"))).ToList();
            var distance = totals.Select(t => new Bar(Label(t.PeriodStart), UnitConversions.FromMeters(t.Meters, unit),
                UnitConversions.FromMeters(t.Meters, unit).ToString("0"))).ToList();
            var tss = totals.Select(t => new Bar(Label(t.PeriodStart), t.Tss, t.Tss.ToString("0"))).ToList();
            ChartsLoaded?.Invoke(hours, distance, tss, UnitConversions.Label(unit));

            var curve = TrainingLoad.FitnessFatigue(entries, from, today);
            var fitness = curve.Select((p, i) => new SeriesDownsampler.Point(i * 86400, p.Fitness)).ToList();
            var fatigue = curve.Select((p, i) => new SeriesDownsampler.Point(i * 86400, p.Fatigue)).ToList();
            LoadCurveLoaded?.Invoke(fitness, fatigue);

            Tiles.Clear();
            var current = totals[^1];
            var previous = totals.Count > 1 ? totals[^2] : null;
            var period = monthly ? "THIS MONTH" : "THIS WEEK";
            Tiles.Add(new($"{period}: TIME", Format.Hours(current.Seconds), previous is null ? "" : $"last {Format.Hours(previous.Seconds)}"));
            Tiles.Add(new($"{period}: DISTANCE", UnitConversions.FromMeters(current.Meters, unit).ToString("0.0"), UnitConversions.Label(unit)));
            Tiles.Add(new($"{period}: TSS", current.Tss.ToString("0"), previous is null ? "" : $"last {previous.Tss:0}"));
            if (curve.Count > 0)
            {
                var last = curve[^1];
                Tiles.Add(new("FITNESS (CTL)", last.Fitness.ToString("0"), "42-day"));
                Tiles.Add(new("FATIGUE (ATL)", last.Fatigue.ToString("0"), "7-day"));
                Tiles.Add(new("FORM (TSB)", last.Form.ToString("+0;-0;0"), ""));
            }
            var withoutTss = entries.Count(e => e.Tss is null && e.Start >= new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue)));
            LoadNote = "Training load is Training Stress Score (TSS), calculated from Normalized Power and the FTP you had when you rode. " +
                       "Fitness and fatigue are 42- and 7-day weighted averages of daily TSS; form is fitness minus fatigue." +
                       (withoutTss > 0 ? $" {withoutTss} ride(s) in this range had no FTP and add no load." : "") +
                       " Simulated rides are excluded.";
            StatusText = entries.Count == 0 ? "No rides in this range yet." : "";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load trends: {ex.Message}";
            _services.Log.Error(DiagnosticCategory.App, $"Trends failed: {ex}");
        }
    }
}
