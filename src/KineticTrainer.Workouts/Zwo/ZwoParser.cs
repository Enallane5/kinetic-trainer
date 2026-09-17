using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using KineticTrainer.Workouts.Model;

namespace KineticTrainer.Workouts.Zwo;

public enum ZwoIssueSeverity
{
    /// <summary>Imported, but something was ignored or adjusted.</summary>
    Warning,

    /// <summary>The file cannot be imported.</summary>
    Error,
}

public sealed record ZwoIssue(ZwoIssueSeverity Severity, string Message)
{
    public override string ToString() => $"{Severity}: {Message}";
}

public sealed record ZwoParseResult(Workout? Workout, IReadOnlyList<ZwoIssue> Issues)
{
    public bool Success => Workout is not null;
    public IEnumerable<ZwoIssue> Warnings => Issues.Where(i => i.Severity == ZwoIssueSeverity.Warning);
    public IEnumerable<ZwoIssue> Errors => Issues.Where(i => i.Severity == ZwoIssueSeverity.Error);
}

/// <summary>
/// Reads Zwift .zwo workout files. Zwift never published a schema, so the rules follow the community reference
/// (h4l/zwift-workout-file-reference) and GoldenCheetah's importer: names are matched case-insensitively, numbers are
/// parsed invariantly as doubles and durations rounded to whole seconds, and anything not understood is reported
/// instead of silently changing the workout. Features that change what the numbers mean (distance-based durations,
/// FTP overrides, run/swim workouts) are refused.
/// </summary>
public static class ZwoParser
{
    public const int MaxFileBytes = 2_000_000;
    public const int MaxWorkoutSeconds = 24 * 3600;

    /// <summary>Largest accepted power fraction (1000 % FTP); larger numbers are almost certainly watts.</summary>
    public const double MaxPowerFraction = 10.0;

    private static readonly HashSet<string> IgnoredHeaderElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "author", "name", "description", "sportType", "durationType", "tags", "workout", "category", "subcategory",
        "entid", "overrideHash", "painIndex", "WorkoutPlan", "visibleAfterTime", "Level", "Tags", "AuthorIcon",
        "CategoryIndex", "SkipAutoLap", "WorkoutGroupName", "WorkoutGroupId",
    };

    private static readonly string[] FtpOverrideElements = ["ftpOverride", "ftpMaleOverride", "ftpFemaleOverride", "setFtpAtPercentage"];

    public static ZwoParseResult ParseFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Fail($"File not found: {path}");
        }
        if (info.Length > MaxFileBytes)
        {
            return Fail($"The file is too large to be a workout ({info.Length:N0} bytes).");
        }
        return Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
    }

    public static ZwoParseResult Parse(string xml, string? fallbackName = null)
    {
        var issues = new List<ZwoIssue>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Fail("The file is empty.");
        }
        if (xml.Length > MaxFileBytes)
        {
            return Fail("The file is too large to be a workout.");
        }

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            return Fail($"Not valid XML: {ex.Message}");
        }

        var root = doc.Root;
        if (root is null || !Is(root, "workout_file"))
        {
            return Fail("Not a Zwift workout file (the root element must be <workout_file>).");
        }

        var sport = Child(root, "sportType")?.Value.Trim();
        if (!string.IsNullOrEmpty(sport) && !sport.Equals("bike", StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"This is a '{sport}' workout; only bike workouts are supported.");
        }
        var durationType = Child(root, "durationType")?.Value.Trim();
        if (!string.IsNullOrEmpty(durationType) && !durationType.Equals("time", StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"Workouts measured by '{durationType}' are not supported; only time-based workouts are.");
        }
        foreach (var o in FtpOverrideElements)
        {
            if (Child(root, o) is not null)
            {
                return Fail($"The file uses <{o}>, which changes how its power targets are read; this is not supported.");
            }
        }
        foreach (var e in root.Elements())
        {
            if (!IgnoredHeaderElements.Contains(e.Name.LocalName))
            {
                issues.Add(Warn($"Ignored header element <{e.Name.LocalName}>."));
            }
        }

        var workoutElement = Child(root, "workout");
        if (workoutElement is null)
        {
            return Fail("The file has no <workout> section.", issues);
        }

        var name = Child(root, "name")?.Value.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = string.IsNullOrWhiteSpace(fallbackName) ? "Imported workout" : fallbackName.Trim();
        }
        var tags = Child(root, "tags")?.Elements().Where(t => Is(t, "tag"))
            .Select(t => (Attr(t, "name") ?? t.Value).Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        var steps = new List<WorkoutStep>();
        var workoutEvents = new List<WorkoutTextEvent>();
        var index = 0;
        foreach (var e in workoutElement.Elements())
        {
            index++;
            var where = $"step {index} (<{e.Name.LocalName}>)";
            try
            {
                if (Is(e, "textevent"))
                {
                    if (ReadTextEvent(e, where, issues) is { } te)
                    {
                        workoutEvents.Add(te);
                    }
                    continue;
                }
                if (ReadStep(e, where, issues) is { } step)
                {
                    steps.Add(step);
                }
            }
            catch (ZwoFormatException ex)
            {
                issues.Add(new ZwoIssue(ZwoIssueSeverity.Error, $"{where}: {ex.Message}"));
            }
        }

        if (issues.Any(i => i.Severity == ZwoIssueSeverity.Error))
        {
            return new ZwoParseResult(null, issues);
        }
        if (steps.Count == 0)
        {
            return Fail("The workout has no steps.", issues);
        }
        var total = steps.Sum(s => s.TotalSeconds);
        if (total > MaxWorkoutSeconds)
        {
            return Fail($"The workout is longer than 24 hours ({total / 3600.0:0.#} h).", issues);
        }
        foreach (var te in workoutEvents.Where(te => te.OffsetSeconds > total))
        {
            issues.Add(Warn($"A text event at {te.OffsetSeconds} s is after the end of the workout and will not be shown."));
        }

        var workout = new Workout
        {
            Name = name,
            Author = NullIfEmpty(Child(root, "author")?.Value),
            Description = NullIfEmpty(Child(root, "description")?.Value),
            Tags = tags,
            Steps = steps,
            WorkoutTextEvents = workoutEvents.OrderBy(t => t.OffsetSeconds).ToArray(),
            PowerUnit = WorkoutPowerUnit.FtpFraction,
            FtpTest = Workout.FtpTestFromTags(tags),
        };
        return new ZwoParseResult(workout, issues);
    }

    private static WorkoutStep? ReadStep(XElement e, string where, List<ZwoIssue> issues)
    {
        var kindName = e.Name.LocalName.ToLowerInvariant();
        WorkoutStep? step;
        switch (kindName)
        {
            case "warmup":
            case "cooldown":
            case "ramp":
            {
                var kind = kindName switch { "warmup" => WorkoutStepKind.Warmup, "cooldown" => WorkoutStepKind.Cooldown, _ => WorkoutStepKind.Ramp };
                var duration = RequiredDuration(e, "Duration");
                double low, high;
                var lowAttr = Number(e, "PowerLow");
                var highAttr = Number(e, "PowerHigh");
                if (lowAttr is null && highAttr is null)
                {
                    var flat = Number(e, "Power") ?? throw new ZwoFormatException("has no power target (PowerLow/PowerHigh or Power).");
                    low = high = flat;
                }
                else
                {
                    low = lowAttr ?? highAttr!.Value;
                    high = highAttr ?? low;
                }
                CheckPower(low, "PowerLow");
                CheckPower(high, "PowerHigh");
                if (kind == WorkoutStepKind.Warmup && low > high)
                {
                    issues.Add(Warn($"{where}: warm-up power goes down ({Pct(low)} → {Pct(high)}); kept as written."));
                }
                if (kind == WorkoutStepKind.Cooldown && low < high)
                {
                    issues.Add(Warn($"{where}: cool-down power goes up ({Pct(low)} → {Pct(high)}); kept as written."));
                }
                step = new WorkoutStep { Kind = kind, DurationSeconds = duration, PowerLow = low, PowerHigh = high };
                break;
            }
            case "steadystate":
            case "solidstate":
            {
                if (kindName == "solidstate")
                {
                    issues.Add(Warn($"{where}: <SolidState> is undocumented; treated as a steady step."));
                }
                var duration = RequiredDuration(e, "Duration");
                var power = Number(e, "Power");
                if (power is null)
                {
                    var lo = Number(e, "PowerLow");
                    var hi = Number(e, "PowerHigh");
                    if (lo is null && hi is null)
                    {
                        throw new ZwoFormatException("has no power target.");
                    }
                    power = ((lo ?? hi!.Value) + (hi ?? lo!.Value)) / 2;
                }
                CheckPower(power.Value, "Power");
                step = WorkoutStep.Steady(duration, power.Value);
                break;
            }
            case "intervalst":
            {
                var repeat = (int)Math.Round(Number(e, "Repeat") ?? 1, MidpointRounding.AwayFromZero);
                if (repeat < 1)
                {
                    repeat = 1;
                }
                if (repeat > 500)
                {
                    throw new ZwoFormatException($"Repeat={repeat} is not plausible.");
                }
                var on = RequiredDuration(e, "OnDuration");
                var off = Duration(e, "OffDuration") ?? 0;
                if (off < 0)
                {
                    throw new ZwoFormatException("OffDuration is negative.");
                }
                var (onLow, onHigh) = PhasePower(e, "OnPower", "PowerOnLow", "PowerOnHigh", required: true);
                var (offLow, offHigh) = off > 0 ? PhasePower(e, "OffPower", "PowerOffLow", "PowerOffHigh", required: true) : (0d, 0d);
                step = new WorkoutStep
                {
                    Kind = WorkoutStepKind.Intervals,
                    Repeat = repeat,
                    OnDurationSeconds = on,
                    OffDurationSeconds = off,
                    OnPowerLow = onLow,
                    OnPowerHigh = onHigh,
                    OffPowerLow = offLow,
                    OffPowerHigh = offHigh,
                    CadenceResting = Rpm(e, "CadenceResting"),
                };
                break;
            }
            case "freeride":
                step = WorkoutStep.FreeRide(RequiredDuration(e, "Duration"));
                if (Attr(e, "FlatRoad") is not null)
                {
                    issues.Add(Warn($"{where}: FlatRoad is a Zwift course option and was ignored."));
                }
                break;
            case "maxeffort":
                step = WorkoutStep.MaxEffort(RequiredDuration(e, "Duration"));
                break;
            default:
            {
                // Unknown element: keep the timing if it has a duration, otherwise drop it - and say so either way.
                if (Duration(e, "Duration") is { } d && d > 0)
                {
                    issues.Add(Warn($"{where}: unsupported element, ridden as a free-ride step of {d} s."));
                    step = WorkoutStep.FreeRide(d);
                }
                else
                {
                    issues.Add(Warn($"{where}: unsupported element, skipped."));
                    return null;
                }
                break;
            }
        }

        var cadence = Rpm(e, "Cadence");
        var cadenceLow = Rpm(e, "CadenceLow");
        var cadenceHigh = Rpm(e, "CadenceHigh");
        var events = new List<WorkoutTextEvent>();
        foreach (var child in e.Elements())
        {
            if (Is(child, "textevent"))
            {
                if (ReadTextEvent(child, where, issues) is { } te)
                {
                    if (te.OffsetSeconds >= step.TotalSeconds)
                    {
                        issues.Add(Warn($"{where}: text event at {te.OffsetSeconds} s is past the end of the step ({step.TotalSeconds} s) and will not be shown."));
                    }
                    events.Add(te);
                }
            }
            else
            {
                issues.Add(Warn($"{where}: ignored <{child.Name.LocalName}>."));
            }
        }
        return step with
        {
            Cadence = cadence,
            CadenceLow = cadenceLow,
            CadenceHigh = cadenceHigh,
            TextEvents = events.OrderBy(t => t.OffsetSeconds).ToArray(),
        };
    }

    private static WorkoutTextEvent? ReadTextEvent(XElement e, string where, List<ZwoIssue> issues)
    {
        var message = (Attr(e, "message") ?? Attr(e, "mssage"))?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            issues.Add(Warn($"{where}: a text event has no message and was skipped."));
            return null;
        }
        var raw = Attr(e, "timeoffset");
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!TryNumber(raw, out var v) || v < 0)
            {
                issues.Add(Warn($"{where}: text event offset '{raw}' is not valid; the message was skipped."));
                return null;
            }
            offset = (int)Math.Round(v, MidpointRounding.AwayFromZero);
        }
        return new WorkoutTextEvent(offset, message);
    }

    private static (double Low, double High) PhasePower(XElement e, string flat, string lowName, string highName, bool required)
    {
        if (Number(e, flat) is { } p)
        {
            CheckPower(p, flat);
            return (p, p);
        }
        var lo = Number(e, lowName);
        var hi = Number(e, highName);
        if (lo is null && hi is null)
        {
            if (required)
            {
                throw new ZwoFormatException($"has no {flat} (or {lowName}/{highName}).");
            }
            return (0, 0);
        }
        var low = lo ?? hi!.Value;
        var high = hi ?? low;
        CheckPower(low, lowName);
        CheckPower(high, highName);
        return (low, high);
    }

    private static void CheckPower(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ZwoFormatException($"{name} must be a positive number.");
        }
        if (value > MaxPowerFraction)
        {
            throw new ZwoFormatException($"{name}={value.ToString(CultureInfo.InvariantCulture)} looks like watts; ZWO power must be a fraction of FTP (1.0 = 100 %).");
        }
    }

    private static int RequiredDuration(XElement e, string name)
    {
        var d = Duration(e, name) ?? throw new ZwoFormatException($"has no {name}.");
        if (d <= 0)
        {
            throw new ZwoFormatException($"{name} must be more than zero.");
        }
        return d;
    }

    private static int? Duration(XElement e, string name)
    {
        var v = Number(e, name);
        if (v is null)
        {
            return null;
        }
        if (v.Value > MaxWorkoutSeconds)
        {
            throw new ZwoFormatException($"{name}={v.Value} seconds is not plausible.");
        }
        return (int)Math.Round(v.Value, MidpointRounding.AwayFromZero);
    }

    private static int? Rpm(XElement e, string name)
    {
        var v = Number(e, name);
        if (v is null || v.Value <= 0)
        {
            return null;
        }
        return (int)Math.Round(Math.Min(v.Value, 250), MidpointRounding.AwayFromZero);
    }

    private static double? Number(XElement e, string name)
    {
        var raw = Attr(e, name);
        if (raw is null || raw.Trim().Length == 0)
        {
            return null;
        }
        if (!TryNumber(raw, out var v))
        {
            throw new ZwoFormatException($"{name}='{raw}' is not a number.");
        }
        return v;
    }

    private static bool TryNumber(string raw, out double value) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string? Attr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static XElement? Child(XElement e, string name) =>
        e.Elements().FirstOrDefault(c => Is(c, name));

    private static bool Is(XElement e, string name) =>
        string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Pct(double fraction) => $"{fraction * 100:0}%";

    private static ZwoIssue Warn(string message) => new(ZwoIssueSeverity.Warning, message);

    private static ZwoParseResult Fail(string message, IEnumerable<ZwoIssue>? earlier = null) =>
        new(null, [.. earlier ?? [], new ZwoIssue(ZwoIssueSeverity.Error, message)]);

    private sealed class ZwoFormatException(string message) : Exception(message);
}
