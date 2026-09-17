namespace KineticTrainer.Core.Zones;

/// <summary>
/// One FTP-relative power zone. Fractions are of FTP; a zone covers (Lower, Upper], except zone 1 which starts at 0
/// and the top zone which has no upper bound. <see cref="ColorHex"/> is the single source of truth for zone colours:
/// the UI builds its brushes from it, and the simulator bridge / exports can use it too.
/// </summary>
public sealed record PowerZone(int Number, string Name, string ShortName, double LowerFraction, double? UpperFraction, string ColorHex)
{
    public string Label => $"Z{Number} {Name}";

    public (int Low, int? High) WattRange(int ftp) =>
        (Number == 1 ? 0 : (int)Math.Floor(LowerFraction * ftp) + 1, UpperFraction is { } u ? (int)Math.Floor(u * ftp) : null);

    public string DescribeRange(int ftp)
    {
        var (low, high) = WattRange(ftp);
        return high is { } h ? $"{low}–{h} W" : $"{low}+ W";
    }
}

/// <summary>
/// The standard seven-zone FTP model (Coggan): Active Recovery ≤55 %, Endurance 56–75 %, Tempo 76–90 %,
/// Threshold 91–105 %, VO2max 106–120 %, Anaerobic Capacity 121–150 %, Neuromuscular >150 %.
/// Kept in Core so the live display, ride statistics, history and workouts all agree. Colours follow the Kinetic Fit
/// dark palette (tempo = the Kinetic accent green #78C957) with the Zwift/TrainerRoad zone hue order.
/// </summary>
public static class PowerZones
{
    public static readonly IReadOnlyList<PowerZone> Coggan =
    [
        new(1, "Active Recovery", "Recovery", 0.00, 0.55, "#8E8E8E"),
        new(2, "Endurance", "Endurance", 0.55, 0.75, "#4D9BE6"),
        new(3, "Tempo", "Tempo", 0.75, 0.90, "#78C957"),
        new(4, "Threshold", "Threshold", 0.90, 1.05, "#F2D22E"),
        new(5, "VO2 Max", "VO2 Max", 1.05, 1.20, "#F5963C"),
        new(6, "Anaerobic Capacity", "Anaerobic", 1.20, 1.50, "#E5473F"),
        new(7, "Neuromuscular Power", "Neuromuscular", 1.50, null, "#C65CD8"),
    ];

    public const int MinimumFtp = 50;
    public const int MaximumFtp = 800;

    public static bool IsValidFtp(int? ftp) => ftp is >= MinimumFtp and <= MaximumFtp;

    /// <summary>Zone for a power value, or null when FTP is unknown or power is negative/absent.</summary>
    public static PowerZone? ZoneFor(double? watts, int? ftp)
    {
        if (watts is not { } w || w < 0 || !IsValidFtp(ftp))
        {
            return null;
        }
        return ZoneForFraction(w / ftp!.Value);
    }

    /// <summary>Zone for a fraction of FTP (1.0 = 100 %), e.g. a workout target.</summary>
    public static PowerZone ZoneForFraction(double fraction)
    {
        foreach (var zone in Coggan)
        {
            if (zone.UpperFraction is not { } upper || fraction <= upper + 1e-9)
            {
                return zone;
            }
        }
        return Coggan[^1];
    }

    /// <summary>Percent of FTP, or null.</summary>
    public static double? PercentOfFtp(double? watts, int? ftp) =>
        watts is { } w && IsValidFtp(ftp) ? w / ftp!.Value * 100.0 : null;

    /// <summary>Seconds spent in each zone (index 0 = zone 1) for a 1 Hz power series.</summary>
    public static double[] TimeInZones(IEnumerable<double> oneHzPower, int ftp)
    {
        var result = new double[Coggan.Count];
        if (!IsValidFtp(ftp))
        {
            return result;
        }
        foreach (var p in oneHzPower)
        {
            if (ZoneFor(p, ftp) is { } z)
            {
                result[z.Number - 1] += 1;
            }
        }
        return result;
    }
}
