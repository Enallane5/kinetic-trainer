using System.Reflection;
using KineticTrainer.Workouts.Model;
using KineticTrainer.Workouts.Timeline;
using KineticTrainer.Workouts.Zwo;

namespace KineticTrainer.Workouts.Library;

public enum WorkoutSource
{
    BuiltIn,
    Imported,
    Custom,
}

/// <summary>A workout available to ride, with its ZWO text (the stored form) and planned metrics.</summary>
public sealed class WorkoutEntry(string key, Workout workout, WorkoutSource source, string zwo, long? databaseId = null)
{
    private WorkoutTimeline? _timeline;
    private WorkoutMetrics? _metrics;

    /// <summary>Stable identity: "builtin:file.zwo" or "db:42".</summary>
    public string Key { get; } = key;
    public Workout Workout { get; } = workout;
    public WorkoutSource Source { get; } = source;
    public string Zwo { get; } = zwo;
    public long? DatabaseId { get; } = databaseId;

    public WorkoutTimeline Timeline => _timeline ??= new WorkoutTimeline(Workout);
    public WorkoutMetrics Metrics => _metrics ??= WorkoutMetrics.For(Timeline);
    public string Name => Workout.Name;
    public string Category => Workout.FtpTest != FtpTestKind.None ? WorkoutLibrary.TestCategory : Workout.Category ?? WorkoutLibrary.OtherCategory;
    public bool IsEditable => Source != WorkoutSource.BuiltIn;
}

/// <summary>
/// The workouts that ship with the app (embedded .zwo files authored for this app from published protocols), plus
/// helpers shared by the library UI. User workouts live in the database and are merged in by the app.
/// </summary>
public static class WorkoutLibrary
{
    public const string TestCategory = "Test";
    public const string OtherCategory = "Other";
    private const string ResourcePrefix = "KineticTrainer.Workouts.Library.";

    /// <summary>Display order for categories (easy to hard, tests last); unknown categories follow alphabetically.</summary>
    public static readonly IReadOnlyList<string> CategoryOrder =
        ["Recovery", "Endurance", "Tempo", "Sweet Spot", "Threshold", "VO2 Max", "Anaerobic", "Sprint", TestCategory];

    private static readonly Lazy<IReadOnlyList<WorkoutEntry>> BuiltInCache = new(LoadBuiltIn);

    public static IReadOnlyList<WorkoutEntry> BuiltIn => BuiltInCache.Value;

    public static IEnumerable<WorkoutEntry> FtpTests => BuiltIn.Where(e => e.Workout.FtpTest != FtpTestKind.None);

    public static int CategoryRank(string? category)
    {
        if (category is null)
        {
            return int.MaxValue;
        }
        for (var i = 0; i < CategoryOrder.Count; i++)
        {
            if (string.Equals(CategoryOrder[i], category, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return CategoryOrder.Count;
    }

    public static IEnumerable<WorkoutEntry> Sort(IEnumerable<WorkoutEntry> entries) =>
        entries.OrderBy(e => CategoryRank(e.Category))
            .ThenBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Metrics.IntensityFactor ?? 0)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<WorkoutEntry> LoadBuiltIn()
    {
        var assembly = typeof(WorkoutLibrary).Assembly;
        var list = new List<WorkoutEntry>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".zwo", StringComparison.OrdinalIgnoreCase)))
        {
            var zwo = Read(assembly, name);
            var file = name[ResourcePrefix.Length..];
            var result = ZwoParser.Parse(zwo, Path.GetFileNameWithoutExtension(file));
            if (result.Workout is null)
            {
                // A broken embedded file is a build error in spirit; the unit tests parse every one.
                throw new InvalidDataException($"Built-in workout {file} failed to parse: {string.Join("; ", result.Issues)}");
            }
            list.Add(new WorkoutEntry("builtin:" + file, result.Workout, WorkoutSource.BuiltIn, zwo));
        }
        return Sort(list).ToArray();
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException($"Missing resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
