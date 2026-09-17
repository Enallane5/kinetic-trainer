using System.Globalization;
using System.Text;
using System.Xml;
using KineticTrainer.Workouts.Model;

namespace KineticTrainer.Workouts.Zwo;

/// <summary>
/// Writes Zwift-compatible .zwo files using the element and attribute spellings Zwift itself uses, so exported
/// workouts open in Zwift, TrainerRoad's importer, intervals.icu and similar tools, and re-import here unchanged.
/// </summary>
public static class ZwoWriter
{
    /// <param name="ftpForWatts">Required when the workout is in absolute watts (to convert to FTP fractions).</param>
    public static string Write(Workout workout, int? ftpForWatts = null)
    {
        ArgumentNullException.ThrowIfNull(workout);
        if (workout.PowerUnit == WorkoutPowerUnit.Watts)
        {
            if (ftpForWatts is not { } ftp)
            {
                throw new ArgumentException("An FTP is needed to export a watts-based workout to ZWO.", nameof(ftpForWatts));
            }
            workout = workout.ToFtpFractions(ftp);
        }

        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            OmitXmlDeclaration = true,
            NewLineChars = "\n",
            Encoding = new UTF8Encoding(false),
        };
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteStartElement("workout_file");
            w.WriteElementString("author", workout.Author ?? "Kinetic Trainer");
            w.WriteElementString("name", workout.Name);
            w.WriteElementString("description", workout.Description ?? "");
            w.WriteElementString("sportType", "bike");

            var tags = workout.Tags.ToList();
            if (workout.FtpTest != FtpTestKind.None && Workout.FtpTestFromTags(tags) == FtpTestKind.None)
            {
                tags.Add(Workout.FtpTestTag(workout.FtpTest));
            }
            w.WriteStartElement("tags");
            foreach (var tag in tags)
            {
                w.WriteStartElement("tag");
                w.WriteAttributeString("name", tag);
                w.WriteEndElement();
            }
            w.WriteEndElement();

            w.WriteStartElement("workout");
            foreach (var te in workout.WorkoutTextEvents)
            {
                WriteTextEvent(w, te);
            }
            foreach (var s in workout.Steps)
            {
                WriteStep(w, s);
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }
        return sb.ToString() + "\n";
    }

    public static void WriteFile(Workout workout, string path, int? ftpForWatts = null) =>
        File.WriteAllText(path, Write(workout, ftpForWatts), new UTF8Encoding(false));

    private static void WriteStep(XmlWriter w, WorkoutStep s)
    {
        switch (s.Kind)
        {
            case WorkoutStepKind.Warmup:
            case WorkoutStepKind.Cooldown:
            case WorkoutStepKind.Ramp:
                w.WriteStartElement(s.Kind.ToString());
                Int(w, "Duration", s.DurationSeconds);
                Num(w, "PowerLow", s.PowerLow);
                Num(w, "PowerHigh", s.PowerHigh);
                break;
            case WorkoutStepKind.SteadyState:
                w.WriteStartElement("SteadyState");
                Int(w, "Duration", s.DurationSeconds);
                if (Math.Abs(s.PowerLow - s.PowerHigh) < 1e-9)
                {
                    Num(w, "Power", s.PowerLow);
                }
                else
                {
                    Num(w, "Power", (s.PowerLow + s.PowerHigh) / 2);
                }
                break;
            case WorkoutStepKind.Intervals:
                w.WriteStartElement("IntervalsT");
                Int(w, "Repeat", Math.Max(1, s.Repeat));
                Int(w, "OnDuration", s.OnDurationSeconds);
                Int(w, "OffDuration", s.OffDurationSeconds);
                PhasePower(w, "OnPower", "PowerOnLow", "PowerOnHigh", s.OnPowerLow, s.OnPowerHigh);
                PhasePower(w, "OffPower", "PowerOffLow", "PowerOffHigh", s.OffPowerLow, s.OffPowerHigh);
                if (s.CadenceResting is { } cr)
                {
                    Int(w, "CadenceResting", cr);
                }
                break;
            case WorkoutStepKind.FreeRide:
                w.WriteStartElement("FreeRide");
                Int(w, "Duration", s.DurationSeconds);
                break;
            case WorkoutStepKind.MaxEffort:
                w.WriteStartElement("MaxEffort");
                Int(w, "Duration", s.DurationSeconds);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(s), s.Kind, "Unknown step kind.");
        }
        if (s.Cadence is { } c)
        {
            Int(w, "Cadence", c);
        }
        if (s.CadenceLow is { } cl)
        {
            Int(w, "CadenceLow", cl);
        }
        if (s.CadenceHigh is { } ch)
        {
            Int(w, "CadenceHigh", ch);
        }
        foreach (var te in s.TextEvents)
        {
            WriteTextEvent(w, te);
        }
        w.WriteEndElement();
    }

    private static void PhasePower(XmlWriter w, string flat, string lowName, string highName, double low, double high)
    {
        if (Math.Abs(low - high) < 1e-9)
        {
            Num(w, flat, low);
        }
        else
        {
            Num(w, lowName, low);
            Num(w, highName, high);
        }
    }

    private static void WriteTextEvent(XmlWriter w, WorkoutTextEvent te)
    {
        w.WriteStartElement("textevent");
        Int(w, "timeoffset", te.OffsetSeconds);
        w.WriteAttributeString("message", te.Message);
        w.WriteEndElement();
    }

    private static void Int(XmlWriter w, string name, int value) =>
        w.WriteAttributeString(name, value.ToString(CultureInfo.InvariantCulture));

    private static void Num(XmlWriter w, string name, double value) =>
        w.WriteAttributeString(name, Math.Round(value, 4).ToString("0.00##", CultureInfo.InvariantCulture));
}
