using System.Globalization;
using System.Text.Json;
using KineticTrainer.Core.Rides;
using KineticTrainer.Recording;
using Microsoft.Data.Sqlite;

namespace KineticTrainer.Persistence;

/// <summary>One row of the history list.</summary>
public sealed record RideListItem(
    long Id,
    DateTimeOffset StartedAt,
    double ElapsedSeconds,
    double DistanceMeters,
    double? AveragePowerWatts,
    int? MaxPowerWatts,
    double? NormalizedPowerWatts,
    double? AverageCadenceRpm,
    double? AverageSpeedMetersPerSecond,
    bool Simulated,
    string? SensorName,
    string? Protocol,
    string? FitPath,
    string? TcxPath)
{
    public int? Ftp { get; init; }

    public double? IntensityFactor { get; init; }

    public double? TrainingStressScore { get; init; }

    public string? Title { get; init; }

    public double? WorkKilojoules { get; init; }

    public long? ProfileId { get; init; }

    public bool HasWorkout { get; init; }
}

/// <summary>A ride loaded with all its samples.</summary>
public sealed record StoredRide(RideListItem Item, RideSummary Summary, IReadOnlyList<RideSample> Samples, string? Notes)
{
    public string? WorkoutZwo { get; init; }

    public string? ComplianceJson { get; init; }
}

public sealed record CalibrationRecord(long Id, DateTimeOffset PerformedAt, string? SensorName, string? Protocol, bool Succeeded, string? Result,
    double? TargetLowMps, double? TargetHighMps);

public interface IRideRepository
{
    Task<long> SaveAsync(long profileId, RideRecording ride, string? protocol, string? fitPath, string? tcxPath, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<RideListItem>> ListAsync(long profileId, CancellationToken ct = default);

    /// <summary>Rides that started in [from, to), oldest first (for trends).</summary>
    Task<IReadOnlyList<RideListItem>> ListBetweenAsync(long profileId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    Task UpdateTitleAsync(long id, string? title, CancellationToken ct = default);

    Task<StoredRide?> GetAsync(long id, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<long> RecordCalibrationAsync(CalibrationRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<CalibrationRecord>> ListCalibrationsAsync(int limit = 20, CancellationToken ct = default);
}

/// <summary>SQLite-backed ride history. Samples are stored as rows so future versions can query them directly.</summary>
public sealed class RideRepository : IRideRepository
{
    private const string ItemColumns =
        "id, started_utc, elapsed_s, distance_m, avg_power, max_power, normalized_power, avg_cadence, avg_speed, simulated, sensor_name, protocol, " +
        "fit_path, tcx_path, ftp, intensity_factor, tss, title, work_kj, profile_id, (workout_zwo IS NOT NULL)";

    private readonly LocalDatabase _db;

    public RideRepository(LocalDatabase db)
    {
        _db = db;
    }

    public async Task<long> SaveAsync(long profileId, RideRecording ride, string? protocol, string? fitPath, string? tcxPath, CancellationToken ct = default)
    {
        var s = ride.Summary;
        await using var conn = _db.Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        long id;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO rides (started_utc, stopped_utc, elapsed_s, moving_s, distance_m, sample_count, avg_power, max_power, avg_cadence,
                                   max_cadence, avg_speed, max_speed, normalized_power, work_kj, sensor_name, protocol, simulated, fit_path, tcx_path, created_utc,
                                   ftp, zone_seconds, intensity_factor, tss, title, profile_id, workout_zwo, compliance_json)
                VALUES ($started, $stopped, $elapsed, $moving, $distance, $samples, $avgP, $maxP, $avgC, $maxC, $avgV, $maxV, $np, $work, $sensor, $protocol, $sim, $fit, $tcx, $created,
                        $ftp, $zones, $if, $tss, $title, $profile, $zwo, $compliance);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$started", Iso(ride.StartedAt));
            cmd.Parameters.AddWithValue("$stopped", Iso(ride.StoppedAt));
            cmd.Parameters.AddWithValue("$elapsed", s.ElapsedSeconds);
            cmd.Parameters.AddWithValue("$moving", s.MovingSeconds);
            cmd.Parameters.AddWithValue("$distance", s.DistanceMeters);
            cmd.Parameters.AddWithValue("$samples", s.SampleCount);
            cmd.Parameters.AddWithValue("$avgP", Db(s.AveragePowerWatts));
            cmd.Parameters.AddWithValue("$maxP", Db(s.MaxPowerWatts));
            cmd.Parameters.AddWithValue("$avgC", Db(s.AverageCadenceRpm));
            cmd.Parameters.AddWithValue("$maxC", Db(s.MaxCadenceRpm));
            cmd.Parameters.AddWithValue("$avgV", Db(s.AverageSpeedMetersPerSecond));
            cmd.Parameters.AddWithValue("$maxV", Db(s.MaxSpeedMetersPerSecond));
            cmd.Parameters.AddWithValue("$np", Db(s.NormalizedPowerWatts));
            cmd.Parameters.AddWithValue("$work", Db(s.WorkKilojoules));
            cmd.Parameters.AddWithValue("$sensor", Db(ride.SensorName));
            cmd.Parameters.AddWithValue("$protocol", Db(protocol));
            cmd.Parameters.AddWithValue("$sim", ride.Simulated ? 1 : 0);
            cmd.Parameters.AddWithValue("$fit", Db(fitPath));
            cmd.Parameters.AddWithValue("$tcx", Db(tcxPath));
            cmd.Parameters.AddWithValue("$created", Iso(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$ftp", Db(s.Ftp));
            cmd.Parameters.AddWithValue("$zones", s.ZoneSeconds is { } z ? JsonSerializer.Serialize(z) : DBNull.Value);
            cmd.Parameters.AddWithValue("$if", Db(s.IntensityFactor));
            cmd.Parameters.AddWithValue("$tss", Db(s.TrainingStressScore));
            cmd.Parameters.AddWithValue("$title", Db(ride.Title));
            cmd.Parameters.AddWithValue("$profile", profileId);
            cmd.Parameters.AddWithValue("$zwo", Db(ride.WorkoutZwo));
            cmd.Parameters.AddWithValue("$compliance", Db(ride.ComplianceJson));
            id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO ride_samples (ride_id, seq, timestamp_utc, elapsed_s, power, cadence, speed, distance_m)
                VALUES ($ride, $seq, $ts, $elapsed, $power, $cadence, $speed, $distance);
                """;
            var pRide = cmd.Parameters.Add("$ride", SqliteType.Integer);
            var pSeq = cmd.Parameters.Add("$seq", SqliteType.Integer);
            var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
            var pElapsed = cmd.Parameters.Add("$elapsed", SqliteType.Real);
            var pPower = cmd.Parameters.Add("$power", SqliteType.Integer);
            var pCadence = cmd.Parameters.Add("$cadence", SqliteType.Real);
            var pSpeed = cmd.Parameters.Add("$speed", SqliteType.Real);
            var pDistance = cmd.Parameters.Add("$distance", SqliteType.Real);
            pRide.Value = id;
            var seq = 0;
            foreach (var sample in ride.Samples)
            {
                pSeq.Value = seq++;
                pTs.Value = Iso(sample.Timestamp);
                pElapsed.Value = sample.ElapsedSeconds;
                pPower.Value = Db(sample.PowerWatts);
                pCadence.Value = Db(sample.CadenceRpm);
                pSpeed.Value = Db(sample.SpeedMetersPerSecond);
                pDistance.Value = sample.DistanceMeters;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        if (s.PowerBests is { Count: > 0 } bests)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO ride_power_bests (ride_id, duration_s, watts) VALUES ($ride, $d, $w)";
            var pRide = cmd.Parameters.Add("$ride", SqliteType.Integer);
            var pD = cmd.Parameters.Add("$d", SqliteType.Integer);
            var pW = cmd.Parameters.Add("$w", SqliteType.Real);
            pRide.Value = id;
            foreach (var (duration, watts) in bests)
            {
                pD.Value = duration;
                pW.Value = watts;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    public async Task<IReadOnlyList<RideListItem>> ListAsync(long profileId, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ItemColumns} FROM rides WHERE profile_id = $profile ORDER BY started_utc DESC";
        cmd.Parameters.AddWithValue("$profile", profileId);
        return await ReadItemsAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RideListItem>> ListBetweenAsync(long profileId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ItemColumns} FROM rides WHERE profile_id = $profile AND started_utc >= $from AND started_utc < $to ORDER BY started_utc";
        cmd.Parameters.AddWithValue("$profile", profileId);
        cmd.Parameters.AddWithValue("$from", Iso(from));
        cmd.Parameters.AddWithValue("$to", Iso(to));
        return await ReadItemsAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task UpdateTitleAsync(long id, string? title, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rides SET title = $title WHERE id = $id";
        cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? DBNull.Value : title.Trim());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<StoredRide?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        RideListItem? item;
        RideSummary? summary;
        string? notes, workoutZwo, compliance;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ItemColumns}, stopped_utc, moving_s, sample_count, max_cadence, max_speed, notes, zone_seconds, workout_zwo, compliance_json FROM rides WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }
            item = ReadItem(r);
            const int x = 21; // first extra column
            summary = new RideSummary
            {
                StartTime = item.StartedAt,
                EndTime = ParseIso(r.GetString(x)),
                ElapsedSeconds = item.ElapsedSeconds,
                MovingSeconds = r.GetDouble(x + 1),
                DistanceMeters = item.DistanceMeters,
                SampleCount = (int)r.GetInt64(x + 2),
                AveragePowerWatts = item.AveragePowerWatts,
                MaxPowerWatts = item.MaxPowerWatts,
                AverageCadenceRpm = item.AverageCadenceRpm,
                MaxCadenceRpm = NullableDouble(r, x + 3),
                AverageSpeedMetersPerSecond = item.AverageSpeedMetersPerSecond,
                MaxSpeedMetersPerSecond = NullableDouble(r, x + 4),
                NormalizedPowerWatts = item.NormalizedPowerWatts,
                WorkKilojoules = item.WorkKilojoules,
                Ftp = item.Ftp,
                IntensityFactor = item.IntensityFactor,
                TrainingStressScore = item.TrainingStressScore,
                ZoneSeconds = r.IsDBNull(x + 6) ? null : JsonSerializer.Deserialize<double[]>(r.GetString(x + 6)),
            };
            notes = r.IsDBNull(x + 5) ? null : r.GetString(x + 5);
            workoutZwo = NullableString(r, x + 7);
            compliance = NullableString(r, x + 8);
        }

        var bests = new Dictionary<int, double>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT duration_s, watts FROM ride_power_bests WHERE ride_id = $id ORDER BY duration_s";
            cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                bests[(int)r.GetInt64(0)] = r.GetDouble(1);
            }
        }
        summary = summary with { PowerBests = bests };

        var samples = new List<RideSample>(summary.SampleCount);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT timestamp_utc, elapsed_s, power, cadence, speed, distance_m FROM ride_samples WHERE ride_id = $id ORDER BY seq";
            cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                samples.Add(new RideSample(
                    ParseIso(r.GetString(0)),
                    r.GetDouble(1),
                    r.IsDBNull(2) ? null : (int)r.GetInt64(2),
                    NullableDouble(r, 3),
                    NullableDouble(r, 4),
                    r.GetDouble(5)));
            }
        }
        return new StoredRide(item, summary, samples, notes) { WorkoutZwo = workoutZwo, ComplianceJson = compliance };
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var sql in new[] { "DELETE FROM ride_samples WHERE ride_id = $id", "DELETE FROM ride_power_bests WHERE ride_id = $id", "DELETE FROM ride_uploads WHERE ride_id = $id", "DELETE FROM rides WHERE id = $id" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> RecordCalibrationAsync(CalibrationRecord record, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO calibrations (performed_utc, sensor_name, protocol, succeeded, result, target_low_mps, target_high_mps)
            VALUES ($at, $sensor, $protocol, $ok, $result, $low, $high);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$at", Iso(record.PerformedAt));
        cmd.Parameters.AddWithValue("$sensor", Db(record.SensorName));
        cmd.Parameters.AddWithValue("$protocol", Db(record.Protocol));
        cmd.Parameters.AddWithValue("$ok", record.Succeeded ? 1 : 0);
        cmd.Parameters.AddWithValue("$result", Db(record.Result));
        cmd.Parameters.AddWithValue("$low", Db(record.TargetLowMps));
        cmd.Parameters.AddWithValue("$high", Db(record.TargetHighMps));
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<CalibrationRecord>> ListCalibrationsAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, performed_utc, sensor_name, protocol, succeeded, result, target_low_mps, target_high_mps FROM calibrations ORDER BY performed_utc DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<CalibrationRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new CalibrationRecord(r.GetInt64(0), ParseIso(r.GetString(1)), NullableString(r, 2), NullableString(r, 3),
                r.GetInt64(4) != 0, NullableString(r, 5), NullableDouble(r, 6), NullableDouble(r, 7)));
        }
        return list;
    }

    private static async Task<IReadOnlyList<RideListItem>> ReadItemsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<RideListItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadItem(r));
        }
        return list;
    }

    private static RideListItem ReadItem(SqliteDataReader r) => new(
        r.GetInt64(0),
        ParseIso(r.GetString(1)),
        r.GetDouble(2),
        r.GetDouble(3),
        NullableDouble(r, 4),
        r.IsDBNull(5) ? null : (int)r.GetInt64(5),
        NullableDouble(r, 6),
        NullableDouble(r, 7),
        NullableDouble(r, 8),
        r.GetInt64(9) != 0,
        NullableString(r, 10),
        NullableString(r, 11),
        NullableString(r, 12),
        NullableString(r, 13))
    {
        Ftp = r.IsDBNull(14) ? null : (int)r.GetInt64(14),
        IntensityFactor = NullableDouble(r, 15),
        TrainingStressScore = NullableDouble(r, 16),
        Title = NullableString(r, 17),
        WorkKilojoules = NullableDouble(r, 18),
        ProfileId = r.IsDBNull(19) ? null : r.GetInt64(19),
        HasWorkout = r.GetInt64(20) != 0,
    };

    private static object Db<T>(T? v) where T : struct => v is { } x ? x : DBNull.Value;

    private static object Db(string? v) => v is null ? DBNull.Value : v;

    private static double? NullableDouble(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
