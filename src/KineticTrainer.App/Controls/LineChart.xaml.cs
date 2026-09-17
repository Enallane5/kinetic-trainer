using KineticTrainer.Core.Statistics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KineticTrainer.App.Controls;

/// <summary>
/// Dependency-free line chart: x = seconds, y = value. Downsamples to the pixel width (min/max per column) so a
/// multi-hour ride at 2 Hz still draws instantly, and redraws only on data or size changes.
/// </summary>
public sealed partial class LineChart : UserControl
{
    private IReadOnlyList<SeriesDownsampler.Point> _series = [];
    private IReadOnlyList<SeriesDownsampler.Point> _series2 = [];
    private double? _reference;
    private double _fixedMax;

    public LineChart()
    {
        InitializeComponent();
        Line.Stroke = new SolidColorBrush(Services.KineticColors.Power);
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Unit { get; set; } = "";

    public Brush Stroke
    {
        get => Line.Stroke;
        set => Line.Stroke = value;
    }

    /// <summary>Force the y axis to at least this value (e.g. FTP so the scale stays stable); 0 = auto.</summary>
    public double MinimumMax
    {
        get => _fixedMax;
        set
        {
            _fixedMax = value;
            Redraw();
        }
    }

    /// <summary>Optional horizontal reference line (e.g. FTP or a workout target).</summary>
    public double? Reference
    {
        get => _reference;
        set
        {
            _reference = value;
            Redraw();
        }
    }

    /// <summary>Fixed x range (e.g. -300..0 for a rolling five-minute graph); null = fit the data.</summary>
    public (double Min, double Max)? XRange { get; set; }

    /// <summary>Formats x-axis labels; default is elapsed time.</summary>
    public Func<double, string>? XLabel { get; set; }

    public void SetSeries(IReadOnlyList<SeriesDownsampler.Point> series)
    {
        _series = series;
        Redraw();
    }

    /// <summary>Optional second line on the same axes (e.g. fatigue next to fitness).</summary>
    public void SetSecondSeries(IReadOnlyList<SeriesDownsampler.Point> series, Brush stroke)
    {
        _series2 = series;
        Line2.Stroke = stroke;
        Redraw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        var w = PlotCanvas.ActualWidth;
        var h = PlotCanvas.ActualHeight;
        if (w < 4 || h < 4)
        {
            return;
        }
        var points = SeriesDownsampler.MinMax(_series, Math.Max(1, (int)w));
        var points2 = SeriesDownsampler.MinMax(_series2, Math.Max(1, (int)w));
        var yMax = Math.Max(_fixedMax, points.Concat(points2).Select(p => p.Y).DefaultIfEmpty(0).Max());
        if (_reference is { } r)
        {
            yMax = Math.Max(yMax, r);
        }
        yMax = yMax <= 0 ? 1 : yMax * 1.05;
        var x0 = XRange?.Min ?? (points.Count == 0 ? 0 : points[0].X);
        var x1 = XRange?.Max ?? (points.Count == 0 ? 1 : Math.Max(points[^1].X, x0 + 1));
        if (x1 <= x0)
        {
            x1 = x0 + 1;
        }

        var pc = new PointCollection();
        foreach (var p in points)
        {
            if (p.X < x0 || p.X > x1)
            {
                continue;
            }
            pc.Add(new Point((p.X - x0) / (x1 - x0) * w, h - Math.Clamp(p.Y, 0, yMax) / yMax * h));
        }
        Line.Points = pc;
        var pc2 = new PointCollection();
        foreach (var p in points2)
        {
            if (p.X >= x0 && p.X <= x1)
            {
                pc2.Add(new Point((p.X - x0) / (x1 - x0) * w, h - Math.Clamp(p.Y, 0, yMax) / yMax * h));
            }
        }
        Line2.Points = pc2;

        MidLine.X1 = 0; MidLine.X2 = w; MidLine.Y1 = MidLine.Y2 = h / 2;
        if (_reference is { } rv)
        {
            RefLine.Visibility = Visibility.Visible;
            RefLine.X1 = 0; RefLine.X2 = w; RefLine.Y1 = RefLine.Y2 = h - rv / yMax * h;
        }
        else
        {
            RefLine.Visibility = Visibility.Collapsed;
        }

        MaxLabel.Text = $"{yMax:0} {Unit}";
        MidLabel.Text = $"{yMax / 2:0}";
        MinLabel.Text = "0";
        StartLabel.Text = XLabel?.Invoke(x0) ?? FormatTime(x0);
        EndLabel.Text = XLabel?.Invoke(x1) ?? FormatTime(x1);
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
