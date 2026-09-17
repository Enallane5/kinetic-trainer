using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Units;
using KineticTrainer.Persistence;

namespace KineticTrainer.App.ViewModels;

public sealed partial class RideRow : ObservableObject
{
    public RideRow(RideListItem item, Core.Settings.AppSettings settings)
    {
        Item = item;
        Id = item.Id;
        DateText = item.StartedAt.ToLocalTime().ToString("ddd d MMM yyyy  ·  h:mm tt").ToUpperInvariant();
        Title = string.IsNullOrWhiteSpace(item.Title) ? "Free ride" : item.Title!;
        DurationText = Format.Duration(item.ElapsedSeconds);
        DistanceText = $"{UnitConversions.FromMeters(item.DistanceMeters, settings.DistanceUnit):0.00} {UnitConversions.Label(settings.DistanceUnit)}";
        AvgPowerText = item.AveragePowerWatts is { } p ? $"{p:0} W" : "—";
        NpText = item.NormalizedPowerWatts is { } np ? $"NP {np:0} W" : "";
        WorkText = item.WorkKilojoules is { } kj ? $"{kj:0} kJ" : "—";
        IfText = Format.Number(item.IntensityFactor, "0.00");
        TssText = Format.Number(item.TrainingStressScore);
        Badge = item.Simulated ? "SIMULATED" : item.HasWorkout ? "WORKOUT" : "";
    }

    public RideListItem Item { get; }
    public long Id { get; }
    public string DateText { get; }
    public string Title { get; }
    public string DurationText { get; }
    public string DistanceText { get; }
    public string AvgPowerText { get; }
    public string NpText { get; }
    public string WorkText { get; }
    public string IfText { get; }
    public string TssText { get; }
    public string Badge { get; }
    public bool HasBadge => Badge.Length > 0;
    public bool IsSimulated => Item.Simulated;
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private List<RideRow> _all = [];

    public HistoryViewModel(AppServices services)
    {
        _services = services;
        services.ActiveProfileChanged += x => _ = RefreshAsync();
        services.RideSaved += x => _ = RefreshAsync();
    }

    public ObservableCollection<RideRow> Rides { get; } = [];

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _riderName = "";
    [ObservableProperty] private string _totalsText = "";
    [ObservableProperty] private bool _showSimulated = true;
    [ObservableProperty] private string _filterText = "";

    /// <summary>Raised when a ride row is opened; the page navigates to the detail view.</summary>
    public event Action<long>? OpenRequested;

    partial void OnShowSimulatedChanged(bool value) => ApplyFilter();

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        RiderName = _services.ActiveProfile.Name;
        if (_services.Rides is null)
        {
            StatusText = "Ride history is unavailable because the local database could not be opened.";
            return;
        }
        IsBusy = true;
        try
        {
            var items = await _services.Rides.ListAsync(_services.ActiveProfile.Id);
            _all = items.Select(i => new RideRow(i, _services.Settings)).ToList();
            var real = items.Where(i => !i.Simulated).ToList();
            var dist = UnitConversions.FromMeters(real.Sum(i => i.DistanceMeters), _services.Settings.DistanceUnit);
            TotalsText = $"{real.Count} ride{(real.Count == 1 ? "" : "s")}  ·  {Format.Hours(real.Sum(i => i.ElapsedSeconds))}  ·  " +
                         $"{dist:0} {UnitConversions.Label(_services.Settings.DistanceUnit)}  ·  {real.Sum(i => i.WorkKilojoules ?? 0):0} kJ";
            ApplyFilter();
            StatusText = items.Count == 0 ? "No rides yet. Start a ride on the Ride page; it is saved here when you stop." : "";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load rides: {ex.Message}";
            _services.Log.Error(DiagnosticCategory.App, $"History load failed: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Rides.Clear();
        var text = FilterText.Trim();
        foreach (var r in _all)
        {
            if (!ShowSimulated && r.IsSimulated)
            {
                continue;
            }
            if (text.Length > 0 && !r.Title.Contains(text, StringComparison.OrdinalIgnoreCase) && !r.DateText.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Rides.Add(r);
        }
    }

    [RelayCommand]
    private void Open(RideRow? row)
    {
        if (row is not null)
        {
            OpenRequested?.Invoke(row.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(RideRow? row)
    {
        if (row is null || _services.Rides is null)
        {
            return;
        }
        try
        {
            await _services.Rides.DeleteAsync(row.Id);
            _all.Remove(row);
            Rides.Remove(row);
            StatusText = "Ride deleted from history (exported files were kept).";
            _services.Log.Info(DiagnosticCategory.App, $"Ride {row.Id} deleted from history");
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ShowFiles(RideRow? row) => ShowFile(_services, row?.Item.FitPath, s => StatusText = s);

    public static void ShowFile(AppServices services, string? path, Action<string> onError)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(services.ExportFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", services.ExportFolder) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            onError(ex.Message);
        }
    }
}
