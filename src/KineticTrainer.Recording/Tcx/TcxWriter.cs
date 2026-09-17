using System.Globalization;
using System.Xml.Linq;

namespace KineticTrainer.Recording.Tcx;

/// <summary>
/// Writes a Garmin Training Center XML (TCX v2) activity with power and cadence in the standard
/// ActivityExtension v2 elements. Readable by Strava, Garmin Connect, GoldenCheetah and most others.
/// </summary>
public static class TcxWriter
{
    public static readonly XNamespace Tcx = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    public static readonly XNamespace Ext = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static string Write(RideRecording ride)
    {
        var s = ride.Summary;
        var lap = new XElement(Tcx + "Lap",
            new XAttribute("StartTime", Time(ride.StartedAt)),
            new XElement(Tcx + "TotalTimeSeconds", Num(s.ElapsedSeconds)),
            new XElement(Tcx + "DistanceMeters", Num(s.DistanceMeters)));
        if (s.MaxSpeedMetersPerSecond is { } maxSpeed && double.IsFinite(maxSpeed))
        {
            lap.Add(new XElement(Tcx + "MaximumSpeed", Num(maxSpeed)));
        }
        // Calories is mandatory in ActivityLap_t; cycling convention: kJ of work ~= kcal burned.
        lap.Add(new XElement(Tcx + "Calories", s.WorkKilojoules is { } kj && double.IsFinite(kj) ? ((int)Math.Clamp(Math.Round(kj), 0, 65535)).ToString(CultureInfo.InvariantCulture) : "0"));
        lap.Add(new XElement(Tcx + "Intensity", "Active"));
        if (s.AverageCadenceRpm is { } avgCad && double.IsFinite(avgCad))
        {
            lap.Add(new XElement(Tcx + "Cadence", Cadence(avgCad)));
        }
        lap.Add(new XElement(Tcx + "TriggerMethod", "Manual"));

        var track = new XElement(Tcx + "Track");
        foreach (var sample in ride.Samples)
        {
            var tp = new XElement(Tcx + "Trackpoint",
                new XElement(Tcx + "Time", Time(sample.Timestamp)),
                new XElement(Tcx + "DistanceMeters", Num(sample.DistanceMeters)));
            if (sample.CadenceRpm is { } c && double.IsFinite(c))
            {
                tp.Add(new XElement(Tcx + "Cadence", Cadence(c)));
            }
            var tpx = new XElement(Ext + "TPX");
            if (sample.SpeedMetersPerSecond is { } v && double.IsFinite(v))
            {
                tpx.Add(new XElement(Ext + "Speed", Num(v)));
            }
            if (sample.PowerWatts is { } p)
            {
                tpx.Add(new XElement(Ext + "Watts", Watts(p)));
            }
            if (tpx.HasElements)
            {
                tp.Add(new XElement(Tcx + "Extensions", tpx));
            }
            track.Add(tp);
        }
        lap.Add(track);

        var lx = new XElement(Ext + "LX");
        if (s.AverageSpeedMetersPerSecond is { } avgSpeed && double.IsFinite(avgSpeed))
        {
            lx.Add(new XElement(Ext + "AvgSpeed", Num(avgSpeed)));
        }
        if (s.AveragePowerWatts is { } avgPower)
        {
            lx.Add(new XElement(Ext + "AvgWatts", Watts(avgPower)));
        }
        if (s.MaxPowerWatts is { } maxPower)
        {
            lx.Add(new XElement(Ext + "MaxWatts", Watts(maxPower)));
        }
        if (lx.HasElements)
        {
            lap.Add(new XElement(Tcx + "Extensions", lx));
        }

        var activity = new XElement(Tcx + "Activity",
            new XAttribute("Sport", "Biking"),
            new XElement(Tcx + "Id", Time(ride.StartedAt)),
            lap,
            new XElement(Tcx + "Creator",
                new XAttribute(Xsi + "type", "Device_t"),
                new XElement(Tcx + "Name", ride.SensorName is { } n ? $"Kinetic Trainer ({n})" : "Kinetic Trainer"),
                new XElement(Tcx + "UnitId", "0"),
                new XElement(Tcx + "ProductID", "0"),
                new XElement(Tcx + "Version",
                    new XElement(Tcx + "VersionMajor", "0"),
                    new XElement(Tcx + "VersionMinor", "1"),
                    new XElement(Tcx + "BuildMajor", "0"),
                    new XElement(Tcx + "BuildMinor", "0"))));

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Tcx + "TrainingCenterDatabase",
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XAttribute(XNamespace.Xmlns + "ns3", Ext),
                new XAttribute(Xsi + "schemaLocation",
                    "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2 http://www.garmin.com/xmlschemas/TrainingCenterDatabasev2.xsd " +
                    "http://www.garmin.com/xmlschemas/ActivityExtension/v2 http://www.garmin.com/xmlschemas/ActivityExtensionv2.xsd"),
                new XElement(Tcx + "Activities", activity)));
        return doc.Declaration + Environment.NewLine + doc.ToString(SaveOptions.None);
    }

    private static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>xsd:double; callers skip non-finite values, this is a last line of defence.</summary>
    private static string Num(double v) => double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "0";

    /// <summary>CadenceValue_t is unsignedByte limited to 254.</summary>
    private static string Cadence(double rpm) => ((int)Math.Clamp(Math.Round(rpm), 0, 254)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Watts are xsd:unsignedShort.</summary>
    private static string Watts(double w) => double.IsFinite(w) ? ((int)Math.Clamp(Math.Round(w), 0, 65535)).ToString(CultureInfo.InvariantCulture) : "0";
}
