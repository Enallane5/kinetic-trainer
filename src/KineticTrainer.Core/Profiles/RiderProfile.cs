using KineticTrainer.Core.Zones;

namespace KineticTrainer.Core.Profiles;

public enum RiderSex
{
    Unspecified,
    Female,
    Male,
}

/// <summary>
/// A local rider account. Everything personal (FTP, body metrics, rides, FTP history, and later linked services such
/// as Strava) hangs off <see cref="Id"/>, so several people can share one PC without mixing data.
/// </summary>
public sealed record RiderProfile
{
    public long Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Functional Threshold Power, watts. Null until entered or measured.</summary>
    public int? Ftp { get; init; }

    public double? WeightKg { get; init; }

    public double? HeightCm { get; init; }

    public int? BirthYear { get; init; }

    public RiderSex Sex { get; init; } = RiderSex.Unspecified;

    public int? MaxHeartRate { get; init; }

    public int? RestingHeartRate { get; init; }

    /// <summary>Badge colour in the UI (hex).</summary>
    public string Color { get; init; } = "#78C957";

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedUtc { get; init; }

    public double? FtpWattsPerKg => Ftp is { } f && WeightKg is > 0 ? f / WeightKg.Value : null;

    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 40)
        {
            errors.Add("Name must be 1-40 characters.");
        }
        if (Ftp is not null && !PowerZones.IsValidFtp(Ftp))
        {
            errors.Add($"FTP must be between {PowerZones.MinimumFtp} and {PowerZones.MaximumFtp} W.");
        }
        if (WeightKg is not null and (< 25 or > 250))
        {
            errors.Add("Weight must be between 25 and 250 kg.");
        }
        if (HeightCm is not null and (< 100 or > 230))
        {
            errors.Add("Height must be between 100 and 230 cm.");
        }
        if (BirthYear is { } y && (y < 1900 || y > DateTime.UtcNow.Year))
        {
            errors.Add("Birth year is not valid.");
        }
        if (MaxHeartRate is not null and (< 100 or > 230))
        {
            errors.Add("Max heart rate must be between 100 and 230 bpm.");
        }
        return errors;
    }

    /// <summary>Palette for new profiles, starting with Kinetic green.</summary>
    public static readonly IReadOnlyList<string> Palette = ["#78C957", "#3B8EEA", "#F2923B", "#B06EE0", "#E5534B", "#F2CB3B", "#4DC6C1"];
}

/// <summary>Best average power the rider has held for a duration, and where.</summary>
public sealed record PowerBest(int DurationSeconds, double Watts, long RideId, DateTimeOffset RideDate);

/// <summary>Lifetime records for a profile, computed from its ride history.</summary>
public sealed record ProfileRecords
{
    public int RideCount { get; init; }

    public double TotalSeconds { get; init; }

    public double TotalDistanceMeters { get; init; }

    public double TotalWorkKilojoules { get; init; }

    public int? MaxPowerWatts { get; init; }

    public DateTimeOffset? MaxPowerDate { get; init; }

    public double? MaxCadenceRpm { get; init; }

    public double? MaxSpeedMetersPerSecond { get; init; }

    public double? LongestRideSeconds { get; init; }

    public double? LongestRideMeters { get; init; }

    public IReadOnlyList<PowerBest> PowerBests { get; init; } = [];

    public static readonly ProfileRecords Empty = new();
}
