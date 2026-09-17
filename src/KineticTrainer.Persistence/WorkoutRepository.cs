using System.Globalization;

namespace KineticTrainer.Persistence;

/// <summary>A workout saved by the rider. The ZWO text is the stored form; the Workouts layer parses it.</summary>
public sealed record StoredWorkout(
    long Id,
    long? ProfileId,
    string Name,
    string? Category,
    string Source,
    string Zwo,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public interface IWorkoutRepository
{
    /// <summary>Workouts owned by the profile plus shared ones (no owner), newest first.</summary>
    Task<IReadOnlyList<StoredWorkout>> ListAsync(long profileId, CancellationToken ct = default);

    Task<StoredWorkout?> GetAsync(long id, CancellationToken ct = default);

    Task<long> AddAsync(long? profileId, string name, string? category, string source, string zwo, CancellationToken ct = default);

    Task UpdateAsync(long id, string name, string? category, string zwo, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);
}

/// <summary>User workouts (imported .zwo files and ones built in the app). Built-in workouts are not stored.</summary>
public sealed class WorkoutRepository : IWorkoutRepository
{
    public const string SourceImported = "imported";
    public const string SourceCustom = "custom";

    /// <summary>Stored ZWO text is capped (the parser also refuses larger files).</summary>
    public const int MaxZwoLength = 2_000_000;

    private const string Columns = "id, profile_id, name, category, source, zwo, created_utc, updated_utc";

    private readonly LocalDatabase _db;

    public WorkoutRepository(LocalDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<StoredWorkout>> ListAsync(long profileId, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM workouts WHERE profile_id = $p OR profile_id IS NULL ORDER BY updated_utc DESC, id DESC";
        cmd.Parameters.AddWithValue("$p", profileId);
        var list = new List<StoredWorkout>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Read(r));
        }
        return list;
    }

    public async Task<StoredWorkout?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM workouts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task<long> AddAsync(long? profileId, string name, string? category, string source, string zwo, CancellationToken ct = default)
    {
        Validate(name, zwo);
        var now = Iso(DateTimeOffset.UtcNow);
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workouts (profile_id, name, category, source, zwo, created_utc, updated_utc)
            VALUES ($p, $name, $cat, $source, $zwo, $now, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", (object?)profileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$cat", (object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$zwo", zwo);
        cmd.Parameters.AddWithValue("$now", now);
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task UpdateAsync(long id, string name, string? category, string zwo, CancellationToken ct = default)
    {
        Validate(name, zwo);
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE workouts SET name = $name, category = $cat, zwo = $zwo, updated_utc = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$cat", (object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$zwo", zwo);
        cmd.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException($"Workout {id} does not exist.");
        }
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM workouts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Validate(string name, string zwo)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A workout needs a name.", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(zwo) || zwo.Length > MaxZwoLength)
        {
            throw new ArgumentException("The workout file is empty or too large.", nameof(zwo));
        }
    }

    private static StoredWorkout Read(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetInt64(0),
        r.IsDBNull(1) ? null : r.GetInt64(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        Parse(r.GetString(6)),
        Parse(r.GetString(7)));

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
