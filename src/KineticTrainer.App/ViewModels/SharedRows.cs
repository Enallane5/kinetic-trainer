using KineticTrainer.App.Services;
using KineticTrainer.Core.Zones;
using Microsoft.UI.Xaml.Media;

namespace KineticTrainer.App.ViewModels;

/// <summary>One row of a time-in-zone chart.</summary>
public sealed record ZoneBar(string Label, string RangeText, string TimeText, string PercentText, double Fraction, SolidColorBrush Brush)
{
    /// <summary>Bar length in pixels for a 360 px track (x:Bind-friendly).</summary>
    public double BarWidth => Math.Max(2, Fraction * 360);

    public static IReadOnlyList<ZoneBar> From(IReadOnlyList<double>? zoneSeconds, int? ftp)
    {
        if (zoneSeconds is null || zoneSeconds.Count == 0)
        {
            return [];
        }
        var total = zoneSeconds.Sum();
        var max = zoneSeconds.Max();
        return PowerZones.Coggan.Select((z, i) =>
        {
            var secs = i < zoneSeconds.Count ? zoneSeconds[i] : 0;
            return new ZoneBar(
                z.Label,
                ftp is { } f && PowerZones.IsValidFtp(f) ? z.DescribeRange(f) : $"{z.LowerFraction * 100:0}–{(z.UpperFraction is { } u ? $"{u * 100:0}%" : "+")}",
                Format.Duration(secs),
                total > 0 ? $"{secs / total * 100:0}%" : "0%",
                max > 0 ? secs / max : 0,
                KineticColors.ZoneBrush(z.Number));
        }).ToList();
    }
}

/// <summary>A zone with its watt range for the rider's FTP.</summary>
public sealed record ZoneRow(string Label, string RangeText, string PercentText, SolidColorBrush Brush)
{
    public static IReadOnlyList<ZoneRow> For(int? ftp) => PowerZones.Coggan.Select(z => new ZoneRow(
        z.Label,
        ftp is { } f && PowerZones.IsValidFtp(f) ? z.DescribeRange(f) : "set FTP",
        z.UpperFraction is { } u ? (z.Number == 1 ? $"≤ {u * 100:0}%" : $"{z.LowerFraction * 100 + 1:0}–{u * 100:0}%") : $"> {z.LowerFraction * 100:0}%",
        KineticColors.ZoneBrush(z.Number))).ToList();
}

public static class Format
{
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>"1 h 20 min" style for totals.</summary>
    public static string Hours(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min";
    }

    public static string Number(double? value, string format = "0") => value is { } v && double.IsFinite(v) ? v.ToString(format) : "—";
}
