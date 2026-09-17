using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KineticTrainer.App.Services;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Persistence;
using KineticTrainer.Workouts.Library;
using KineticTrainer.Workouts.Model;
using KineticTrainer.Workouts.Timeline;
using KineticTrainer.Workouts.Zwo;
using Microsoft.UI.Xaml.Media;

namespace KineticTrainer.App.ViewModels;

public sealed class WorkoutItem
{
    public WorkoutItem(WorkoutEntry entry)
    {
        Entry = entry;
        var m = entry.Metrics;
        DurationText = Format.Duration(entry.Timeline.TotalSeconds);
        IfText = Format.Number(m.IntensityFactor, "0.00");
        TssText = Format.Number(m.TrainingStressScore);
        Badge = entry.Source switch
        {
            WorkoutSource.Imported => "IMPORTED",
            WorkoutSource.Custom => "MY WORKOUT",
            _ => entry.Workout.FtpTest != FtpTestKind.None ? "FTP TEST" : "",
        };
    }

    public WorkoutEntry Entry { get; }
    public string Name => Entry.Name;
    public string Category => Entry.Category.ToUpperInvariant();
    public string Description => Entry.Workout.Description ?? "";
    public WorkoutTimeline Timeline => Entry.Timeline;
    public string DurationText { get; }
    public string IfText { get; }
    public string TssText { get; }
    public string Badge { get; }
    public bool HasBadge => Badge.Length > 0;
}

public sealed record StepLine(string Time, string Text, SolidColorBrush Brush);

public sealed partial class WorkoutsViewModel : ObservableObject
{
    public const string AllCategory = "All";
    public const string MineCategory = "My workouts";

    private readonly AppServices _services;
    private List<WorkoutItem> _all = [];
    private bool _loaded;

    public WorkoutsViewModel(AppServices services)
    {
        _services = services;
        services.ActiveProfileChanged += p =>
        {
            ChartFtp = services.EffectiveFtp;
            _ = LoadAsync();
        };
        services.SettingsChanged += s => services.Dispatcher.TryEnqueue(() => ColorByZone = s.ColorProfilesByZone);
        ChartFtp = services.EffectiveFtp;
        ColorByZone = services.Settings.ColorProfilesByZone;
    }

    public ObservableCollection<WorkoutItem> Items { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];
    public ObservableCollection<StepLine> DetailSteps { get; } = [];
    public ObservableCollection<ZoneBar> DetailZones { get; } = [];
    public IReadOnlyList<string> SortOptions { get; } = ["Category", "Name", "Duration", "IF", "TSS"];

    [ObservableProperty] private string _selectedCategory = AllCategory;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private WorkoutItem? _selected;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _chartFtp;
    [ObservableProperty] private bool _colorByZone = true;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private string _detailStats = "";
    [ObservableProperty] private string _testText = "";

    public event Action<WorkoutEntry?>? EditRequested;

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSortIndexChanged(int value) => ApplyFilter();

    partial void OnSelectedChanged(WorkoutItem? value) => ShowDetail(value);

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            await LoadAsync();
        }
    }

    public async Task LoadAsync()
    {
        _loaded = true;
        var entries = new List<WorkoutEntry>(WorkoutLibrary.BuiltIn);
        if (_services.Workouts is { } repo && _services.ActiveProfile.Id > 0)
        {
            try
            {
                foreach (var stored in await repo.ListAsync(_services.ActiveProfile.Id))
                {
                    var parsed = ZwoParser.Parse(stored.Zwo, stored.Name);
                    if (parsed.Workout is null)
                    {
                        _services.Log.Warn(DiagnosticCategory.App, $"Stored workout {stored.Id} could not be read: {string.Join("; ", parsed.Errors)}");
                        continue;
                    }
                    var workout = parsed.Workout with { Name = stored.Name };
                    if (stored.Category is { } cat && workout.Category != cat)
                    {
                        workout = workout with { Tags = [cat, .. workout.Tags.Where(t => !string.Equals(t, cat, StringComparison.OrdinalIgnoreCase))] };
                    }
                    var source = stored.Source == WorkoutRepository.SourceImported ? WorkoutSource.Imported : WorkoutSource.Custom;
                    entries.Add(new WorkoutEntry("db:" + stored.Id, workout, source, stored.Zwo, stored.Id));
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Could not load your workouts: {ex.Message}";
                _services.Log.Error(DiagnosticCategory.App, $"Workout load failed: {ex}");
            }
        }
        _all = WorkoutLibrary.Sort(entries).Select(e => new WorkoutItem(e)).ToList();

        var keep = SelectedCategory;
        Categories.Clear();
        Categories.Add(AllCategory);
        if (_all.Any(i => i.Entry.Source != WorkoutSource.BuiltIn))
        {
            Categories.Add(MineCategory);
        }
        foreach (var c in _all.Select(i => i.Entry.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(WorkoutLibrary.CategoryRank).ThenBy(c => c))
        {
            Categories.Add(c);
        }
        SelectedCategory = Categories.Contains(keep) ? keep : AllCategory;
        var selectedKey = Selected?.Entry.Key;
        ApplyFilter();
        Selected = Items.FirstOrDefault(i => i.Entry.Key == selectedKey) ?? Items.FirstOrDefault();
    }

    /// <summary>Shows only the FTP tests (from the Profile page).</summary>
    public void ShowTests() => SelectedCategory = WorkoutLibrary.TestCategory;

    private void ApplyFilter()
    {
        IEnumerable<WorkoutItem> q = _all;
        if (SelectedCategory == MineCategory)
        {
            q = q.Where(i => i.Entry.Source != WorkoutSource.BuiltIn);
        }
        else if (SelectedCategory != AllCategory)
        {
            q = q.Where(i => string.Equals(i.Entry.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }
        var text = SearchText.Trim();
        if (text.Length > 0)
        {
            q = q.Where(i => i.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        q = SortIndex switch
        {
            1 => q.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            2 => q.OrderBy(i => i.Timeline.TotalSeconds),
            3 => q.OrderByDescending(i => i.Entry.Metrics.IntensityFactor ?? 0),
            4 => q.OrderByDescending(i => i.Entry.Metrics.TrainingStressScore ?? 0),
            _ => q,
        };
        var selected = Selected;
        Items.Clear();
        foreach (var i in q)
        {
            Items.Add(i);
        }
        if (selected is not null && Items.Contains(selected))
        {
            Selected = selected;
        }
    }

    private void ShowDetail(WorkoutItem? item)
    {
        HasSelection = item is not null;
        DetailSteps.Clear();
        DetailZones.Clear();
        if (item is null)
        {
            return;
        }
        var e = item.Entry;
        var ftp = _services.EffectiveFtp;
        var metrics = WorkoutMetrics.For(e.Timeline, ftp);
        CanEdit = e.IsEditable;
        DetailStats = $"{Format.Duration(e.Timeline.TotalSeconds)}   ·   IF {Format.Number(metrics.IntensityFactor, "0.00")}   ·   TSS {Format.Number(metrics.TrainingStressScore)}" +
                      (metrics.WorkKilojoules is { } kj ? $"   ·   ≈{kj:0} kJ at {ftp} W FTP" : "") +
                      (_services.HasFtp ? "" : $"   (no FTP set: watts use {AppServices.FallbackFtp} W)");
        foreach (var z in ZoneBar.From(metrics.ZoneSeconds.Select(s => (double)s).ToList(), ftp).Where(z => z.Fraction > 0))
        {
            DetailZones.Add(z);
        }
        foreach (var seg in CollapseForDisplay(e.Timeline))
        {
            DetailSteps.Add(seg);
        }
        TestText = e.Workout.FtpTest switch
        {
            FtpTestKind.TwentyMinute => "FTP test: after a structured warm-up, ride the 20-minute effort as hard as you can hold steadily. Your FTP is estimated as 95% of its average power, and you can save it to your profile when you finish.",
            FtpTestKind.Ramp => "FTP test: the target rises every minute. Keep matching it until you can't, then press End ramp (or ease off and it ends by itself). Your FTP is estimated as 75% of your best 1-minute power.",
            FtpTestKind.EightMinute => "FTP test: two 8-minute all-out efforts with a recovery between. Your FTP is estimated as 90% of the better effort's average power.",
            _ => "",
        };
    }

    /// <summary>Step list with interval blocks summarised on one line.</summary>
    private IEnumerable<StepLine> CollapseForDisplay(WorkoutTimeline t)
    {
        var ftp = _services.EffectiveFtp;
        var steps = t.Workout.Steps;
        var segIndex = 0;
        for (var si = 0; si < steps.Count; si++)
        {
            var step = steps[si];
            var first = t.Segments.Skip(segIndex).FirstOrDefault(s => s.StepIndex == si);
            if (first is null)
            {
                continue;
            }
            var count = t.Segments.Count(s => s.StepIndex == si);
            segIndex = first.Index + count;
            var fraction = t.ToFraction(first.AveragePower, ftp) ?? 0;
            var brush = first.HasTarget ? KineticColors.ZoneBrush(Core.Zones.PowerZones.ZoneForFraction(fraction).Number) : new SolidColorBrush(KineticColors.TextSecondary);
            string text;
            if (step.Kind == WorkoutStepKind.Intervals)
            {
                var on = t.Segments[first.Index];
                var off = count > 1 ? t.Segments[first.Index + 1] : null;
                text = $"{step.Repeat} × {Format.Duration(step.OnDurationSeconds)} at {t.DescribeTargetWithPercent(on, ftp)}" +
                       (off is not null && !off.IsOnPhase ? $" / {Format.Duration(step.OffDurationSeconds)} at {t.DescribeTargetWithPercent(off, ftp)}" : "") +
                       (step.Cadence is { } c ? $"  ·  {c} rpm" : "");
            }
            else
            {
                text = $"{first.Label}: {t.DescribeTargetWithPercent(first, ftp)}" + (first.Cadence is { } c ? $"  ·  {c} rpm" : "");
            }
            yield return new StepLine(Format.Duration(step.TotalSeconds), text, brush);
        }
    }

    [RelayCommand]
    private void Ride()
    {
        if (Selected is { } item && _services.Ride.LoadWorkout(item.Entry))
        {
            _services.Navigate("ride");
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_services.Workouts is null)
        {
            StatusText = "Importing needs the local database, which is unavailable.";
            return;
        }
        IReadOnlyList<Windows.Storage.StorageFile> files;
        try
        {
            files = await WindowServices.PickOpenFilesAsync(".zwo");
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the file picker: {ex.Message}";
            return;
        }
        if (files.Count == 0)
        {
            return;
        }
        var messages = new List<string>();
        var imported = 0;
        string? lastKey = null;
        foreach (var file in files)
        {
            try
            {
                var result = ZwoParser.ParseFile(file.Path);
                if (result.Workout is not { } w)
                {
                    messages.Add($"{file.Name}: not imported. {string.Join(" ", result.Errors.Select(e => e.Message))}");
                    continue;
                }
                var zwo = await File.ReadAllTextAsync(file.Path);
                var id = await _services.Workouts.AddAsync(_services.ActiveProfile.Id, w.Name, w.Category, WorkoutRepository.SourceImported, zwo);
                lastKey = "db:" + id;
                imported++;
                var warnings = result.Warnings.ToList();
                if (warnings.Count > 0)
                {
                    messages.Add($"{file.Name}: imported with {warnings.Count} note(s): {string.Join(" ", warnings.Take(3).Select(x => x.Message))}");
                }
                _services.Log.Info(DiagnosticCategory.App, $"Imported workout {w.Name} from {file.Path}");
            }
            catch (Exception ex)
            {
                messages.Add($"{file.Name}: {ex.Message}");
            }
        }
        await LoadAsync();
        if (lastKey is not null)
        {
            SelectedCategory = MineCategory;
            Selected = Items.FirstOrDefault(i => i.Entry.Key == lastKey);
        }
        StatusText = $"Imported {imported} of {files.Count} file(s). " + string.Join("  ", messages);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Selected is not { } item)
        {
            return;
        }
        try
        {
            var file = await WindowServices.PickSaveFileAsync(WindowServices.SafeFileName(item.Name), "Zwift workout", ".zwo");
            if (file is null)
            {
                return;
            }
            await File.WriteAllTextAsync(file.Path, ZwoWriter.Write(item.Entry.Workout, _services.EffectiveFtp));
            StatusText = $"Exported to {file.Path}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void NewWorkout() => EditRequested?.Invoke(null);

    [RelayCommand]
    private void Edit()
    {
        if (Selected is { } item)
        {
            EditRequested?.Invoke(item.Entry);
        }
    }

    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { Entry.DatabaseId: { } id } item || _services.Workouts is null)
        {
            return;
        }
        try
        {
            await _services.Workouts.DeleteAsync(id);
            StatusText = $"Deleted {item.Name}.";
            Selected = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
    }
}
