using KineticTrainer.Core.Zones;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace KineticTrainer.App.Services;

/// <summary>
/// Kinetic Fit palette for code-drawn visuals (charts, profile bars). Mirrors Styles/KineticTheme.xaml; zone colours
/// come from Core so every screen agrees.
/// </summary>
public static class KineticColors
{
    public static readonly Color Background = Hex("#181818");
    public static readonly Color Surface = Hex("#0D0D0D");
    public static readonly Color SurfaceElevated = Hex("#262626");
    public static readonly Color BorderSubtle = Hex("#2E2E2E");
    public static readonly Color TextPrimary = Hex("#FFFFFF");
    public static readonly Color TextSecondary = Hex("#ABABAB");
    public static readonly Color Accent = Hex("#78C957");
    public static readonly Color Warning = Hex("#FCD16B");
    public static readonly Color Danger = Hex("#FF8C8F");
    public static readonly Color Info = Hex("#68A8C8");
    public static readonly Color Power = Hex("#E5DB96");
    public static readonly Color Heart = Hex("#EDB87D");
    public static readonly Color Cadence = Hex("#99C2BF");
    public static readonly Color Cursor = Hex("#ABABAB");

    private static readonly Dictionary<int, SolidColorBrush> ZoneBrushes = [];

    public static SolidColorBrush Brush(Color c) => new(c);

    public static SolidColorBrush Brush(Color c, double opacity) => new(c) { Opacity = opacity };

    public static Color ZoneColor(int zoneNumber) =>
        Hex(PowerZones.Coggan[Math.Clamp(zoneNumber, 1, PowerZones.Coggan.Count) - 1].ColorHex);

    /// <summary>Shared brush per zone (UI thread only).</summary>
    public static SolidColorBrush ZoneBrush(int zoneNumber)
    {
        if (!ZoneBrushes.TryGetValue(zoneNumber, out var b))
        {
            b = new SolidColorBrush(ZoneColor(zoneNumber));
            ZoneBrushes[zoneNumber] = b;
        }
        return b;
    }

    public static Color Hex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 6)
        {
            s = "FF" + s;
        }
        var v = Convert.ToUInt32(s, 16);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
