using KineticTrainer.App.Services;
using Microsoft.UI.Xaml.Media;

namespace KineticTrainer.App.Controls;

/// <summary>x:Bind helper functions for templates.</summary>
public static class ColorFunctions
{
    public static SolidColorBrush Brush(string hex) => new(KineticColors.Hex(hex));

    public static string Label(string name, string unit) => $"{name} ({unit})";
}
