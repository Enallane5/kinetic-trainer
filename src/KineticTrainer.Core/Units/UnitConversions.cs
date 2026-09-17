namespace KineticTrainer.Core.Units;

public enum SpeedUnit
{
    KilometersPerHour,
    MilesPerHour,
}

public enum DistanceUnit
{
    Kilometers,
    Miles,
}

/// <summary>Presentation-only conversions. Everything internal is SI (m, m/s, s, W).</summary>
public static class UnitConversions
{
    public const double MetersPerMile = 1609.344;
    public const double MetersPerSecondToKph = 3.6;
    public const double MetersPerSecondToMph = 3600.0 / MetersPerMile;

    public static double ToKph(double metersPerSecond) => metersPerSecond * MetersPerSecondToKph;

    public static double ToMph(double metersPerSecond) => metersPerSecond * MetersPerSecondToMph;

    public static double KphToMetersPerSecond(double kph) => kph / MetersPerSecondToKph;

    public static double FromMetersPerSecond(double metersPerSecond, SpeedUnit unit) =>
        unit == SpeedUnit.MilesPerHour ? ToMph(metersPerSecond) : ToKph(metersPerSecond);

    public static double FromMeters(double meters, DistanceUnit unit) =>
        unit == DistanceUnit.Miles ? meters / MetersPerMile : meters / 1000.0;

    public static string Label(SpeedUnit unit) => unit == SpeedUnit.MilesPerHour ? "mph" : "km/h";

    public static string Label(DistanceUnit unit) => unit == DistanceUnit.Miles ? "mi" : "km";

    /// <summary>Ground-equivalent speed from roller rotation: circumference x revolutions per second.</summary>
    public static double RollerSpeedMetersPerSecond(double rollerRpm, double rollerCircumferenceMeters) =>
        rollerRpm / 60.0 * rollerCircumferenceMeters;
}
