using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Persistence;
using KineticTrainer.Workouts.Library;
using KineticTrainer.Workouts.Model;
using KineticTrainer.Workouts.Timeline;
using KineticTrainer.Workouts.Zwo;

namespace KineticTrainer.App.ViewModels;

/// <summary>One editable step. Power is entered in % of FTP; times as m:ss (or plain seconds).</summary>
public sealed partial class StepRow : ObservableObject
{
    public static readonly IReadOnlyList<string> KindNames = ["Warm-up", "Steady", "Ramp", "Intervals", "Free ride", "Max effort", "Cool-down"];

    private static readonly WorkoutStepKind[] Kinds =
    [
        WorkoutStepKind.Warmup, WorkoutStepKind.SteadyState, WorkoutStepKind.Ramp, WorkoutStepKind.Intervals,
        WorkoutStepKind.FreeRide, WorkoutStepKind.MaxEffort, WorkoutStepKind.Cooldown,
    ];

    private readonly Action _changed;

    /// <summary>Rider FTP used to show watts next to the percentages.</summary>
    public static int Ftp { get; set; } = 150;

    [ObservableProperty] private int _kindIndex = 1;
    [ObservableProperty] private string _duration = "5:00";
    [ObservableProperty] private double _powerLow = 60;
    [ObservableProperty] private double _powerHigh = 60;
    [ObservableProperty] private double _repeat = 4;
    [ObservableProperty] private string _onDuration = "1:00";
    [ObservableProperty] private string _offDuration = "1:00";
    [ObservableProperty] private double _onPower = 110;
    [ObservableProperty] private double _offPower = 55;
    [ObservableProperty] private double _cadence = double.NaN;

    public StepRow(Action changed)
    {
        _changed = changed;
    }

    /// <summary>Text events and other details that the editor does not show are kept from the original step.</summary>
    public WorkoutStep? Original { get; set; }

    public WorkoutStepKind Kind => Kinds[Math.Clamp(KindIndex, 0, Kinds.Length - 1)];
    public bool IsRamped => Kind is WorkoutStepKind.Warmup or WorkoutStepKind.Ramp or WorkoutStepKind.Cooldown;
    public bool IsSteady => Kind == WorkoutStepKind.SteadyState;
    public bool IsIntervals => Kind == WorkoutStepKind.Intervals;
    public bool HasDuration => !IsIntervals;
    public IReadOnlyList<string> KindOptions => KindNames;

    /// <summary>The percentages as watts for the current rider, e.g. "113–175 W".</summary>
    public string WattsText
    {
        get
        {
            static string W(double pct) => double.IsNaN(pct) ? "?" : $"{Math.Round(pct / 100 * Ftp):0}";
            return Kind switch
            {
                WorkoutStepKind.Intervals => $"{W(OnPower)} / {W(OffPower)} W",
                WorkoutStepKind.SteadyState => $"{W(PowerLow)} W",
                WorkoutStepKind.FreeRide => "no target",
                WorkoutStepKind.MaxEffort => "all out",
                _ => $"{W(PowerLow)}–{W(PowerHigh)} W",
            };
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(WattsText));
        _changed();
    }

    public static StepRow From(WorkoutStep s, Action changed)
    {
        var row = new StepRow(changed)
        {
            Original = s,
            KindIndex = Array.IndexOf(Kinds, s.Kind),
            Duration = FormatTime(s.DurationSeconds),
            PowerLow = Math.Round(s.PowerLow * 100),
            PowerHigh = Math.Round(s.PowerHigh * 100),
            Repeat = Math.Max(1, s.Repeat),
            OnDuration = FormatTime(s.OnDurationSeconds),
            OffDuration = FormatTime(s.OffDurationSeconds),
            OnPower = Math.Round(s.OnPowerLow * 100),
            OffPower = Math.Round(s.OffPowerLow * 100),
            Cadence = s.Cadence is { } c ? c : double.NaN,
        };
        return row;
    }

    public static StepRow New(WorkoutStepKind kind, Action changed)
    {
        var row = new StepRow(changed) { KindIndex = Array.IndexOf(Kinds, kind) };
        switch (kind)
        {
            case WorkoutStepKind.Warmup:
                row.Duration = "10:00";
                row.PowerLow = 45;
                row.PowerHigh = 70;
                break;
            case WorkoutStepKind.Cooldown:
                row.Duration = "5:00";
                row.PowerLow = 60;
                row.PowerHigh = 40;
                break;
            case WorkoutStepKind.Ramp:
                row.PowerLow = 60;
                row.PowerHigh = 90;
                break;
            case WorkoutStepKind.SteadyState:
                row.PowerLow = row.PowerHigh = 75;
                break;
            case WorkoutStepKind.MaxEffort:
                row.Duration = "0:20";
                break;
        }
        return row;
    }

    /// <summary>Builds the step, or throws <see cref="FormatException"/> with a message for the rider.</summary>
    public WorkoutStep Build(int number)
    {
        int? cadence = double.IsNaN(Cadence) || Cadence <= 0 ? null : (int)Math.Round(Cadence);
        var keep = Original is { } o && o.Kind == Kind ? o : null;
        switch (Kind)
        {
            case WorkoutStepKind.Intervals:
            {
                var on = ParseTime(OnDuration, $"Step {number}: interval on-time");
                var off = ParseTime(OffDuration, $"Step {number}: interval off-time", allowZero: true);
                var reps = (int)Math.Round(Repeat);
                if (reps is < 1 or > 100)
                {
                    throw new FormatException($"Step {number}: repeats must be 1-100.");
                }
                CheckPower(OnPower, number);
                CheckPower(OffPower, number);
                return (keep ?? new WorkoutStep { Kind = Kind }) with
                {
                    Kind = Kind,
                    Repeat = reps,
                    OnDurationSeconds = on,
                    OffDurationSeconds = off,
                    OnPowerLow = OnPower / 100,
                    OnPowerHigh = OnPower / 100,
                    OffPowerLow = OffPower / 100,
                    OffPowerHigh = OffPower / 100,
                    Cadence = cadence,
                    TextEvents = keep?.TextEvents ?? [],
                };
            }
            case WorkoutStepKind.FreeRide:
            case WorkoutStepKind.MaxEffort:
                return (keep ?? new WorkoutStep { Kind = Kind }) with
                {
                    Kind = Kind,
                    DurationSeconds = ParseTime(Duration, $"Step {number}: duration"),
                    Cadence = cadence,
                };
            default:
            {
                CheckPower(PowerLow, number);
                var high = IsSteady ? PowerLow : PowerHigh;
                CheckPower(high, number);
                return (keep ?? new WorkoutStep { Kind = Kind }) with
                {
                    Kind = Kind,
                    DurationSeconds = ParseTime(Duration, $"Step {number}: duration"),
                    PowerLow = PowerLow / 100,
                    PowerHigh = high / 100,
                    Cadence = cadence,
                };
            }
        }
    }

    private static void CheckPower(double pct, int number)
    {
        if (double.IsNaN(pct) || pct < 0 || pct > 400)
        {
            throw new FormatException($"Step {number}: power must be 0-400% of FTP.");
        }
    }

    public static string FormatTime(int seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
        : $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>Accepts "90", "1:30" or "1:02:30".</summary>
    public static int ParseTime(string text, string what, bool allowZero = false)
    {
        var parts = (text ?? "").Trim().Split(':');
        var total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || parts.Length > 3)
            {
                throw new FormatException($"{what} '{text}' is not a time (use m:ss).");
            }
            total = (total * 60) + v;
        }
        if (total < (allowZero ? 0 : 1) || total > 6 * 3600)
        {
            throw new FormatException($"{what} must be between {(allowZero ? 0 : 1)} s and 6 hours.");
        }
        return total;
    }

    partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsRamped));
        OnPropertyChanged(nameof(IsSteady));
        OnPropertyChanged(nameof(IsIntervals));
        OnPropertyChanged(nameof(HasDuration));
        Changed();
    }

    partial void OnDurationChanged(string value) => _changed();
    partial void OnPowerLowChanged(double value) => Changed();
    partial void OnPowerHighChanged(double value) => Changed();
    partial void OnRepeatChanged(double value) => _changed();
    partial void OnOnDurationChanged(string value) => _changed();
    partial void OnOffDurationChanged(string value) => _changed();
    partial void OnOnPowerChanged(double value) => Changed();
    partial void OnOffPowerChanged(double value) => Changed();
    partial void OnCadenceChanged(double value) => _changed();
}

/// <summary>Creates or edits a user workout. Built-in workouts are copied, never changed.</summary>
public sealed partial class WorkoutEditorViewModel : ObservableObject
{
    private readonly AppServices _services;
    private long? _databaseId;
    private Workout? _source;

    [ObservableProperty] private string _name = "New workout";
    [ObservableProperty] private string _category = "Custom";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _heading = "New workout";
    [ObservableProperty] private WorkoutTimeline? _preview;
    [ObservableProperty] private StepRow? _selectedStep;

    public WorkoutEditorViewModel(AppServices services)
    {
        _services = services;
        ChartFtp = services.EffectiveFtp;
        StepRow.Ftp = services.EffectiveFtp;
        ColorByZone = services.Settings.ColorProfilesByZone;
    }

    public ObservableCollection<StepRow> Steps { get; } = [];
    public IReadOnlyList<string> CategoryOptions { get; } =
        [.. WorkoutLibrary.CategoryOrder.Where(c => c != WorkoutLibrary.TestCategory), "Custom"];
    public int ChartFtp { get; }

    public string FtpHint => $"Power is % of FTP ({_services.EffectiveFtp} W{(_services.HasFtp ? "" : ", not set yet")}). Times are m:ss.";
    public bool ColorByZone { get; }

    /// <summary>Raised when the editor should close (saved or cancelled).</summary>
    public event Action<bool>? Closed;

    public void Load(WorkoutEntry? entry)
    {
        Steps.Clear();
        if (entry is null)
        {
            _databaseId = null;
            _source = null;
            Heading = "New workout";
            Name = "New workout";
            Category = "Custom";
            Description = "";
            Steps.Add(StepRow.New(WorkoutStepKind.Warmup, Refresh));
            Steps.Add(StepRow.New(WorkoutStepKind.Intervals, Refresh));
            Steps.Add(StepRow.New(WorkoutStepKind.Cooldown, Refresh));
        }
        else
        {
            var w = entry.Workout;
            _source = w;
            _databaseId = entry.IsEditable ? entry.DatabaseId : null;
            Heading = entry.IsEditable ? $"Edit {w.Name}" : $"Copy of {w.Name}";
            Name = entry.IsEditable ? w.Name : $"{w.Name} (copy)";
            Category = w.Category ?? "Custom";
            Description = w.Description ?? "";
            foreach (var s in w.Steps)
            {
                Steps.Add(StepRow.From(s, Refresh));
            }
        }
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            var w = Build();
            Preview = new WorkoutTimeline(w);
            var m = WorkoutMetrics.For(Preview, _services.EffectiveFtp);
            SummaryText = $"{Format.Duration(Preview.TotalSeconds)}  ·  IF {Format.Number(m.IntensityFactor, "0.00")}  ·  TSS {Format.Number(m.TrainingStressScore)}  ·  {Steps.Count} steps";
            ErrorText = "";
        }
        catch (FormatException ex)
        {
            ErrorText = ex.Message;
        }
    }

    private Workout Build()
    {
        if (Steps.Count == 0)
        {
            throw new FormatException("Add at least one step.");
        }
        var steps = Steps.Select((s, i) => s.Build(i + 1)).ToList();
        var total = steps.Sum(s => s.TotalSeconds);
        if (total > ZwoParser.MaxWorkoutSeconds)
        {
            throw new FormatException("The workout is longer than 24 hours.");
        }
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(Category))
        {
            tags.Add(Category.Trim());
        }
        if (_source is not null)
        {
            tags.AddRange(_source.Tags.Skip(1).Where(t => !tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        }
        return new Workout
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Untitled workout" : Name.Trim(),
            Author = _source?.Author ?? _services.ActiveProfile.Name,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Tags = tags,
            Steps = steps,
            WorkoutTextEvents = _source?.WorkoutTextEvents ?? [],
            FtpTest = _source?.FtpTest ?? FtpTestKind.None,
        };
    }

    partial void OnNameChanged(string value) => ErrorText = string.IsNullOrWhiteSpace(value) ? "Give the workout a name." : ErrorText;

    [RelayCommand]
    private void AddStep(string? kind)
    {
        var k = kind switch
        {
            "warmup" => WorkoutStepKind.Warmup,
            "ramp" => WorkoutStepKind.Ramp,
            "intervals" => WorkoutStepKind.Intervals,
            "free" => WorkoutStepKind.FreeRide,
            "max" => WorkoutStepKind.MaxEffort,
            "cooldown" => WorkoutStepKind.Cooldown,
            _ => WorkoutStepKind.SteadyState,
        };
        var row = StepRow.New(k, Refresh);
        var at = SelectedStep is { } sel ? Steps.IndexOf(sel) + 1 : Steps.Count;
        if (k != WorkoutStepKind.Cooldown && SelectedStep is null && Steps.Count > 0 && Steps[^1].Kind == WorkoutStepKind.Cooldown)
        {
            at = Steps.Count - 1; // keep the cool-down last
        }
        Steps.Insert(at, row);
        SelectedStep = row;
        Refresh();
    }

    [RelayCommand]
    private void RemoveStep(StepRow? row)
    {
        if (row is not null && Steps.Remove(row))
        {
            Refresh();
        }
    }

    [RelayCommand]
    private void MoveUp(StepRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(StepRow? row) => Move(row, +1);

    private void Move(StepRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }
        var i = Steps.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Steps.Count)
        {
            return;
        }
        Steps.Move(i, j);
        Refresh();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_services.Workouts is null || _services.ActiveProfile.Id == 0)
        {
            ErrorText = "Saving needs the local database, which is unavailable.";
            return;
        }
        Workout w;
        try
        {
            w = Build();
        }
        catch (FormatException ex)
        {
            ErrorText = ex.Message;
            return;
        }
        try
        {
            var zwo = ZwoWriter.Write(w);
            if (_databaseId is { } id)
            {
                await _services.Workouts.UpdateAsync(id, w.Name, w.Category, zwo);
            }
            else
            {
                _databaseId = await _services.Workouts.AddAsync(_services.ActiveProfile.Id, w.Name, w.Category, WorkoutRepository.SourceCustom, zwo);
            }
            _services.Log.Info(DiagnosticCategory.App, $"Workout saved: {w.Name}");
            await _services.WorkoutsVm.LoadAsync();
            Closed?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorText = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(false);
}
