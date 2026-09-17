using KineticTrainer.App.Services;
using KineticTrainer.Core.Zones;
using KineticTrainer.Workouts.Timeline;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace KineticTrainer.App.Controls;

/// <summary>
/// Draws a workout as a stepped profile (x = time, y = % FTP). Bars are coloured by power zone (TrainerRoad/Zwift
/// style) or with Kinetic Fit's single green-to-black gradient. While riding it shades completed segments, marks
/// the current time with a cursor, and overlays the rider's power. Code-only so it can be used in list templates.
/// </summary>
public sealed class WorkoutProfileChart : UserControl
{
    public static readonly DependencyProperty TimelineProperty =
        DependencyProperty.Register(nameof(Timeline), typeof(WorkoutTimeline), typeof(WorkoutProfileChart), new PropertyMetadata(null, OnLayoutChanged));

    public static readonly DependencyProperty FtpProperty =
        DependencyProperty.Register(nameof(Ftp), typeof(int), typeof(WorkoutProfileChart), new PropertyMetadata(0, OnLayoutChanged));

    public static readonly DependencyProperty ColorByZoneProperty =
        DependencyProperty.Register(nameof(ColorByZone), typeof(bool), typeof(WorkoutProfileChart), new PropertyMetadata(true, OnLayoutChanged));

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(WorkoutProfileChart), new PropertyMetadata(false, OnLayoutChanged));

    public static readonly DependencyProperty ElapsedSecondsProperty =
        DependencyProperty.Register(nameof(ElapsedSeconds), typeof(double), typeof(WorkoutProfileChart), new PropertyMetadata(-1.0, OnElapsedChanged));

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(KineticColors.Surface) };
    private readonly List<(WorkoutSegment Segment, Path Shape)> _bars = [];
    private readonly Line _cursor = new() { Stroke = new SolidColorBrush(KineticColors.TextPrimary), StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Polygon _marker = new() { Fill = new SolidColorBrush(KineticColors.Accent), Visibility = Visibility.Collapsed };
    private readonly Polyline _trace = new() { Stroke = new SolidColorBrush(KineticColors.Power), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
    private readonly Line _ftpLine = new() { Stroke = new SolidColorBrush(KineticColors.TextSecondary), StrokeThickness = 1, StrokeDashArray = [4, 4], Opacity = 0.7 };
    private readonly TextBlock _ftpLabel = new() { Text = "FTP", FontSize = 11, Foreground = new SolidColorBrush(KineticColors.TextSecondary) };
    private readonly TextBlock _endLabel = new() { FontSize = 11, Foreground = new SolidColorBrush(KineticColors.TextSecondary) };
    private double?[] _power = [];
    private double _yMax = 1.3;
    private int _currentIndex = -2;

    public WorkoutProfileChart()
    {
        Content = _canvas;
        _canvas.SizeChanged += (_, _) => Rebuild();
        MinHeight = 24;
        IsHitTestVisible = false;
    }

    public WorkoutTimeline? Timeline
    {
        get => (WorkoutTimeline?)GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    /// <summary>Rider FTP in watts (needed only for watts-based workouts and the power overlay); 0 = unknown.</summary>
    public int Ftp
    {
        get => (int)GetValue(FtpProperty);
        set => SetValue(FtpProperty, value);
    }

    public bool ColorByZone
    {
        get => (bool)GetValue(ColorByZoneProperty);
        set => SetValue(ColorByZoneProperty, value);
    }

    /// <summary>Thumbnail mode: no labels or FTP line.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>Workout clock in seconds; negative = preview (no cursor or shading).</summary>
    public double ElapsedSeconds
    {
        get => (double)GetValue(ElapsedSecondsProperty);
        set => SetValue(ElapsedSecondsProperty, value);
    }

    /// <summary>Rider power per workout second (watts), drawn over the profile.</summary>
    public void SetPower(double?[] powerBySecond)
    {
        _power = powerBySecond;
        DrawTrace();
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WorkoutProfileChart)d).Rebuild();

    private static void OnElapsedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WorkoutProfileChart)d).UpdateProgress();

    private double Fraction(double? v)
    {
        if (v is not { } x || Timeline is not { } t)
        {
            return 0;
        }
        return t.ToFraction(x, Ftp > 0 ? Ftp : null) ?? 0;
    }

    private void Rebuild()
    {
        _canvas.Children.Clear();
        _bars.Clear();
        _currentIndex = -2;
        var t = Timeline;
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (t is null || t.TotalSeconds <= 0 || w < 4 || h < 4)
        {
            return;
        }

        var maxFraction = t.Segments.Where(s => s.HasTarget).Select(s => Math.Max(Fraction(s.PowerStart), Fraction(s.PowerEnd))).DefaultIfEmpty(1).Max();
        _yMax = Math.Max(1.25, Math.Min(maxFraction, WorkoutTimeline.MaxDisplayThreshold)) * 1.08;
        double X(double second) => second / t.TotalSeconds * w;
        double Y(double fraction) => h - (Math.Min(fraction, _yMax) / _yMax * h);

        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, h), MappingMode = BrushMappingMode.Absolute };
        gradient.GradientStops.Add(new GradientStop { Color = KineticColors.Accent, Offset = 0.1 * h / h });
        gradient.GradientStops.Add(new GradientStop { Color = KineticColors.Surface, Offset = 1.0 });
        var gap = Compact ? 0 : Math.Min(1.0, w / t.TotalSeconds * 0.5);

        foreach (var seg in t.Segments)
        {
            var geometry = new PathGeometry();
            Brush fill;
            var x0 = X(seg.StartSecond);
            var x1 = Math.Max(x0 + 0.5, X(seg.EndSecond) - gap);
            if (!seg.HasTarget)
            {
                var top = seg.Role == SegmentRole.MaxEffort ? Y(_yMax * 0.95) : Y(0.3);
                geometry.Figures.Add(Rect(x0, top, x1, h));
                fill = seg.Role == SegmentRole.MaxEffort
                    ? (ColorByZone ? KineticColors.ZoneBrush(7) : gradient)
                    : new SolidColorBrush(KineticColors.TextSecondary) { Opacity = 0.35 };
                AddBar(seg, geometry, fill);
                continue;
            }
            var a = Fraction(seg.PowerStart);
            var b = Fraction(seg.PowerEnd);
            if (!ColorByZone)
            {
                var fig = new PathFigure { StartPoint = new Point(x0, h), IsClosed = true };
                fig.Segments.Add(new LineSegment { Point = new Point(x0, Y(a)) });
                fig.Segments.Add(new LineSegment { Point = new Point(x1, Y(b)) });
                fig.Segments.Add(new LineSegment { Point = new Point(x1, h) });
                geometry.Figures.Add(fig);
                AddBar(seg, geometry, gradient);
                continue;
            }
            if (Math.Abs(a - b) < 1e-9)
            {
                geometry.Figures.Add(Rect(x0, Y(a), x1, h));
                AddBar(seg, geometry, KineticColors.ZoneBrush(PowerZones.ZoneForFraction(a).Number));
                continue;
            }
            // Ramps: slices so the colour follows the zone as the target climbs or falls.
            var slices = Math.Clamp(seg.DurationSeconds / 10, 2, 24);
            for (var i = 0; i < slices; i++)
            {
                var sx0 = x0 + ((x1 - x0) * i / slices);
                // Overlap by half a pixel so anti-aliasing does not leave hairline seams between slices.
                var sx1 = Math.Min(x1, x0 + ((x1 - x0) * (i + 1) / slices) + 0.5);
                var fa = a + ((b - a) * i / slices);
                var fb = a + ((b - a) * (i + 1) / slices);
                var sliceGeometry = new PathGeometry();
                var fig = new PathFigure { StartPoint = new Point(sx0, h), IsClosed = true };
                fig.Segments.Add(new LineSegment { Point = new Point(sx0, Y(fa)) });
                fig.Segments.Add(new LineSegment { Point = new Point(sx1, Y(fb)) });
                fig.Segments.Add(new LineSegment { Point = new Point(sx1, h) });
                sliceGeometry.Figures.Add(fig);
                AddBar(seg, sliceGeometry, KineticColors.ZoneBrush(PowerZones.ZoneForFraction((fa + fb) / 2).Number));
            }
        }

        if (!Compact)
        {
            var fy = Y(1.0);
            _ftpLine.X1 = 0;
            _ftpLine.X2 = w;
            _ftpLine.Y1 = _ftpLine.Y2 = fy;
            _canvas.Children.Add(_ftpLine);
            Canvas.SetLeft(_ftpLabel, 4);
            Canvas.SetTop(_ftpLabel, Math.Max(0, fy - 16));
            _canvas.Children.Add(_ftpLabel);
            _endLabel.Text = FormatTime(t.TotalSeconds);
            _endLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_endLabel, Math.Max(0, w - _endLabel.DesiredSize.Width - 4));
            Canvas.SetTop(_endLabel, 2);
            _canvas.Children.Add(_endLabel);
        }
        _canvas.Children.Add(_trace);
        _canvas.Children.Add(_cursor);
        _canvas.Children.Add(_marker);
        DrawTrace();
        UpdateProgress();
    }

    private void AddBar(WorkoutSegment seg, PathGeometry geometry, Brush fill)
    {
        var path = new Path { Data = geometry, Fill = fill };
        _bars.Add((seg, path));
        _canvas.Children.Add(path);
    }

    private static PathFigure Rect(double x0, double y0, double x1, double y1)
    {
        var fig = new PathFigure { StartPoint = new Point(x0, y1), IsClosed = true };
        fig.Segments.Add(new LineSegment { Point = new Point(x0, y0) });
        fig.Segments.Add(new LineSegment { Point = new Point(x1, y0) });
        fig.Segments.Add(new LineSegment { Point = new Point(x1, y1) });
        return fig;
    }

    private void UpdateProgress()
    {
        var t = Timeline;
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (t is null || _bars.Count == 0 || t.TotalSeconds <= 0)
        {
            return;
        }
        var elapsed = ElapsedSeconds;
        if (elapsed < 0)
        {
            _cursor.Visibility = Visibility.Collapsed;
            _marker.Visibility = Visibility.Collapsed;
            if (_currentIndex != -1)
            {
                foreach (var (_, shape) in _bars)
                {
                    shape.Opacity = 0.95;
                }
                _currentIndex = -1;
            }
            return;
        }
        var x = Math.Clamp(elapsed / t.TotalSeconds * w, 0, w);
        _cursor.X1 = _cursor.X2 = x;
        _cursor.Y1 = 0;
        _cursor.Y2 = h;
        _cursor.Visibility = Visibility.Visible;
        _marker.Points = [new Point(x - 6, 0), new Point(x + 6, 0), new Point(x, 9)];
        _marker.Visibility = Visibility.Visible;

        var index = t.SegmentIndexAt(elapsed);
        if (index == _currentIndex)
        {
            return;
        }
        _currentIndex = index;
        foreach (var (seg, shape) in _bars)
        {
            shape.Opacity = seg.EndSecond <= elapsed ? 0.35 : seg.Index == index ? 1.0 : 0.6;
        }
    }

    private void DrawTrace()
    {
        var t = Timeline;
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        var points = new PointCollection();
        var ftp = Ftp > 0 ? Ftp : 0;
        if (t is null || ftp == 0 || _power.Length == 0 || w < 4 || h < 4)
        {
            _trace.Points = points;
            return;
        }
        // One point per pixel column (average of the seconds it covers); gaps break nothing but are skipped.
        var columns = Math.Max(1, (int)w);
        var perColumn = (double)t.TotalSeconds / columns;
        for (var c = 0; c < columns; c++)
        {
            var s0 = (int)(c * perColumn);
            var s1 = Math.Min(_power.Length, Math.Max(s0 + 1, (int)((c + 1) * perColumn)));
            double sum = 0;
            var n = 0;
            for (var s = s0; s < s1; s++)
            {
                if (_power[s] is { } p)
                {
                    sum += p;
                    n++;
                }
            }
            if (n == 0)
            {
                continue;
            }
            var fraction = sum / n / ftp;
            points.Add(new Point(c + 0.5, h - (Math.Min(fraction, _yMax) / _yMax * h)));
        }
        _trace.Points = points;
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }
}
