using KineticTrainer.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace KineticTrainer.App.Controls;

public sealed record Bar(string Label, double Value, string ValueText);

/// <summary>Simple vertical bar chart (weekly/monthly totals). Code-only, no dependencies.</summary>
public sealed class BarChart : UserControl
{
    private readonly Grid _root = new();
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 80 };
    private readonly Canvas _canvas = new();
    private IReadOnlyList<Bar> _bars = [];

    public BarChart()
    {
        _title.Foreground = new SolidColorBrush(KineticColors.TextSecondary);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(_title);
        var border = new Border
        {
            Background = new SolidColorBrush(KineticColors.Surface),
            BorderBrush = new SolidColorBrush(KineticColors.BorderSubtle),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 8, 8, 4),
            Margin = new Thickness(0, 4, 0, 0),
            Child = _canvas,
        };
        Grid.SetRow(border, 1);
        _root.Children.Add(border);
        Content = _root;
        _canvas.SizeChanged += (_, _) => Redraw();
    }

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public Color BarColor { get; set; } = KineticColors.Accent;

    public void SetBars(IReadOnlyList<Bar> bars)
    {
        _bars = bars;
        Redraw();
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (_bars.Count == 0 || w < 10 || h < 40)
        {
            return;
        }
        const double labelHeight = 18;
        const double valueHeight = 16;
        var plotH = h - labelHeight - valueHeight;
        var max = Math.Max(_bars.Max(b => b.Value), 1e-9);
        var slot = w / _bars.Count;
        var barW = Math.Max(2, slot * 0.62);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(46 / slot));
        var brush = new SolidColorBrush(BarColor);
        var dim = new SolidColorBrush(KineticColors.TextSecondary);
        for (var i = 0; i < _bars.Count; i++)
        {
            var b = _bars[i];
            var bh = b.Value <= 0 ? 0 : Math.Max(2, b.Value / max * plotH);
            var x = (i * slot) + ((slot - barW) / 2);
            var rect = new Rectangle { Width = barW, Height = bh, Fill = brush, Opacity = i == _bars.Count - 1 ? 1.0 : 0.75 };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, valueHeight + plotH - bh);
            _canvas.Children.Add(rect);
            if (b.Value > 0 && slot >= 26)
            {
                var v = new TextBlock { Text = b.ValueText, FontSize = 10, Foreground = dim, Width = slot, TextAlignment = TextAlignment.Center };
                Canvas.SetLeft(v, i * slot);
                Canvas.SetTop(v, valueHeight + plotH - bh - valueHeight);
                _canvas.Children.Add(v);
            }
            if (i % labelEvery == 0 || i == _bars.Count - 1)
            {
                var t = new TextBlock { Text = b.Label, FontSize = 10, Foreground = dim, Width = Math.Max(slot, 46), TextAlignment = TextAlignment.Center };
                Canvas.SetLeft(t, (i * slot) + (slot / 2) - (t.Width / 2));
                Canvas.SetTop(t, h - labelHeight + 2);
                _canvas.Children.Add(t);
            }
        }
    }
}
