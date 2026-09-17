using Microsoft.UI.Xaml;

namespace KineticTrainer.App.Controls;

/// <summary>Small functions for x:Bind expressions (no converters needed).</summary>
public static class Bind
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAll(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfFirstOnly(bool a, bool b) => a && !b ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool And(bool a, bool b) => a && b;
}
