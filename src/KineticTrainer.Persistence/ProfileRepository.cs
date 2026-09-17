using System.Globalization;
using KineticTrainer.Core.Profiles;
using KineticTrainer.Core.Statistics;
using Microsoft.Data.Sqlite;

namespace KineticTrainer.Persistence;

public interface IProfileRepository
{
    Task<IReadOnlyList<RiderProfile>> ListAsync(CancellationToken ct = default);

    Task<RiderProfile?> GetAsync(long id, CancellationToken ct = default);

    Task<RiderProfile> CreateAsync(RiderProfile profile, CancellationToken ct = default);

    /// <param name="ftpSource">Recorded in the FTP history when the FTP changes ("manual", "ftp test", ...).</param>
    Task UpdateAsync(RiderProfile profile, string ftpSource = "manual", string? ftpNote = null, CancellationToken ct = default);

    Task TouchAsync(long id, CancellationToken ct = default);

    /// <summary>Deletes the profile and everything that belongs to it (rides, samples, FTP history, custom workouts).</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Guarantees at least one profile exists and adopts data recorded before profiles existed (rows with no profile).
    /// Returns the profile that should be active.
    /// </summary>
    Task<RiderProfile> EnsureDefaultAsync(long? preferredId, CancellationToken ct = default);

    Task<ProfileRecords> GetRecordsAsync(long profileId, CancellationToken ct = default);
}

public sealed class ProfileRepository : IProfileRepository
{
    private const string Columns = "id, name, ftp, weight_kg, height_cm, birth_year, sex, max_hr, resting_hr, color, created_utc, last_used_utc";

    private readonly LocalDatabase _db;

    public ProfileRepository(LocalDatabase db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<RiderProfile>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM profiles ORDER BY COALESCE(last_used_utc, created_utc) DESC, id";
        var list = new List<RiderProfile>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Read(r));
        }
        return list;
    }

    public async Task<RiderProfile?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task<RiderProfile> CreateAsync(RiderProfile profile, CancellationToken ct = default)
    {
        ThrowIfInvalid(profile);
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profiles (name, ftp, weight_kg, height_cm, birth_year, sex, max_hr, resting_hr, color, created_utc, last_used_utc)
            VALUES ($name, $ftp, $w, $h, $by, $sex, $maxhr, $resthr, $color, $created, $used);
            SELECT last_insert_rowid();
            """;
        var created = profile.CreatedUtc == default ? DateTimeOffset.UtcNow : profile.CreatedUtc;
        Bind(cmd, profile);
        cmd.Parameters.AddWithValue("$created", Iso(created));
        cmd.Parameters.AddWithValue("$used", profile.LastUsedUtc is { } u ? Iso(u) : DBNull.Value);
        var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        if (profile.Ftp is { } ftp)
        {
            await new FtpHistoryRepository(_db).AddAsync(id, ftp, "profile created", created, ct: ct).ConfigureAwait(false);
        }
        return profile with { Id = id, CreatedUtc = created };
    }

    public async Task UpdateAsync(RiderProfile profile, string ftpSource = "manual", string? ftpNote = null, CancellationToken ct = default)
    {
        ThrowIfInvalid(profile);
        var before = await GetAsync(profile.Id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Profile not found.");
        await using (var conn = _db.Open())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE profiles SET name = $name, ftp = $ftp, weight_kg = $w, height_cm = $h, birth_year = $by, sex = $sex,
                                    max_hr = $maxhr, resting_hr = $resthr, color = $color
                WHERE id = $id
                """;
            Bind(cmd, profile);
            cmd.Parameters.AddWithValue("$id", profile.Id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (profile.Ftp is { } ftp && ftp != before.Ftp)
        {
            await new FtpHistoryRepository(_db).AddAsync(profile.Id, ftp, ftpSource, note: ftpNote, ct: ct).ConfigureAwait(false);
        }
    }

    public async Task TouchAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE profiles SET last_used_utc = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var sql in new[]
                 {
                     "DELETE FROM ride_samples WHERE ride_id IN (SELECT id FROM rides WHERE profile_id = $id)",
                     "DELETE FROM ride_power_bests WHERE ride_id IN (SELECT id FROM rides WHERE profile_id = $id)",
                     "DELETE FROM ride_uploads WHERE ride_id IN (SELECT id FROM rides WHERE profile_id = $id)",
                     "DELETE FROM rides WHERE profile_id = $id",
                     "DELETE FROM linked_accounts WHERE profile_id = $id",
                     "DELETE FROM ftp_history WHERE profile_id = $id",
                     "DELETE FROM workouts WHERE profile_id = $id",
                     "DELETE FROM profiles WHERE id = $id",
                 })
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<RiderProfile> EnsureDefaultAsync(long? preferredId, CancellationToken ct = default)
    {
        var profiles = await ListAsync(ct).ConfigureAwait(false);
        RiderProfile active;
        if (profiles.Count == 0)
        {
            active = await CreateAsync(new RiderProfile { Name = "Rider 1", Color = RiderProfile.Palette[0] }, ct).ConfigureAwait(false);
        }
        else
        {
            active = profiles.FirstOrDefault(p => p.Id == preferredId) ?? profiles[0];
        }

        // Adopt rows written before profiles existed.
        await using var conn = _db.Open();
        foreach (var table in new[] { "rides", "ftp_history" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {table} SET profile_id = $id WHERE profile_id IS NULL";
            cmd.Parameters.AddWithValue("$id", active.Id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return active;
    }

    public async Task<ProfileRecords> GetRecordsAsync(long profileId, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        ProfileRecords records;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(elapsed_s), 0), COALESCE(SUM(distance_m), 0), COALESCE(SUM(work_kj), 0),
                       MAX(max_power), MAX(max_cadence), MAX(max_speed), MAX(elapsed_s), MAX(distance_m)
                FROM rides WHERE profile_id = $id AND simulated = 0
                """;
            cmd.Parameters.AddWithValue("$id", profileId);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            records = new ProfileRecords
            {
                RideCount = (int)r.GetInt64(0),
                TotalSeconds = r.GetDouble(1),
                TotalDistanceMeters = r.GetDouble(2),
                TotalWorkKilojoules = r.GetDouble(3),
                MaxPowerWatts = r.IsDBNull(4) ? null : (int)r.GetInt64(4),
                MaxCadenceRpm = r.IsDBNull(5) ? null : r.GetDouble(5),
                MaxSpeedMetersPerSecond = r.IsDBNull(6) ? null : r.GetDouble(6),
                LongestRideSeconds = r.IsDBNull(7) ? null : r.GetDouble(7),
                LongestRideMeters = r.IsDBNull(8) ? null : r.GetDouble(8),
            };
        }

        if (records.MaxPowerWatts is { } maxP)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT started_utc FROM rides WHERE profile_id = $id AND simulated = 0 AND max_power = $p ORDER BY started_utc LIMIT 1";
            cmd.Parameters.AddWithValue("$id", profileId);
            cmd.Parameters.AddWithValue("$p", maxP);
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string s)
            {
                records = records with { MaxPowerDate = ParseIso(s) };
            }
        }

        var bests = new List<PowerBest>();
        await using (var cmd = conn.CreateCommand())
        {
            // Best watts per duration and the (earliest) ride that set it.
            cmd.CommandText = """
                SELECT b.duration_s, b.watts, r.id, r.started_utc
                FROM ride_power_bests b JOIN rides r ON r.id = b.ride_id
                WHERE r.profile_id = $id AND r.simulated = 0
                  AND b.watts = (SELECT MAX(b2.watts) FROM ride_power_bests b2 JOIN rides r2 ON r2.id = b2.ride_id
                                 WHERE r2.profile_id = $id AND r2.simulated = 0 AND b2.duration_s = b.duration_s)
                ORDER BY b.duration_s, r.started_utc
                """;
            cmd.Parameters.AddWithValue("$id", profileId);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var duration = (int)r.GetInt64(0);
                if (bests.Count > 0 && bests[^1].DurationSeconds == duration)
                {
                    continue;
                }
                bests.Add(new PowerBest(duration, r.GetDouble(1), r.GetInt64(2), ParseIso(r.GetString(3))));
            }
        }
        return records with { PowerBests = bests.OrderBy(b => b.DurationSeconds).ToList() };
    }

    private static void ThrowIfInvalid(RiderProfile profile)
    {
        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors));
        }
    }

    private static void Bind(SqliteCommand cmd, RiderProfile p)
    {
        cmd.Parameters.AddWithValue("$name", p.Name.Trim());
        cmd.Parameters.AddWithValue("$ftp", p.Ftp is { } f ? f : DBNull.Value);
        cmd.Parameters.AddWithValue("$w", p.WeightKg is { } w ? w : DBNull.Value);
        cmd.Parameters.AddWithValue("$h", p.HeightCm is { } h ? h : DBNull.Value);
        cmd.Parameters.AddWithValue("$by", p.BirthYear is { } by ? by : DBNull.Value);
        cmd.Parameters.AddWithValue("$sex", p.Sex == RiderSex.Unspecified ? DBNull.Value : p.Sex.ToString());
        cmd.Parameters.AddWithValue("$maxhr", p.MaxHeartRate is { } mh ? mh : DBNull.Value);
        cmd.Parameters.AddWithValue("$resthr", p.RestingHeartRate is { } rh ? rh : DBNull.Value);
        cmd.Parameters.AddWithValue("$color", p.Color);
    }

    private static RiderProfile Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Ftp = r.IsDBNull(2) ? null : (int)r.GetInt64(2),
        WeightKg = r.IsDBNull(3) ? null : r.GetDouble(3),
        HeightCm = r.IsDBNull(4) ? null : r.GetDouble(4),
        BirthYear = r.IsDBNull(5) ? null : (int)r.GetInt64(5),
        Sex = r.IsDBNull(6) ? RiderSex.Unspecified : Enum.TryParse<RiderSex>(r.GetString(6), out var sx) ? sx : RiderSex.Unspecified,
        MaxHeartRate = r.IsDBNull(7) ? null : (int)r.GetInt64(7),
        RestingHeartRate = r.IsDBNull(8) ? null : (int)r.GetInt64(8),
        Color = r.IsDBNull(9) ? RiderProfile.Palette[0] : r.GetString(9),
        CreatedUtc = ParseIso(r.GetString(10)),
        LastUsedUtc = r.IsDBNull(11) ? null : ParseIso(r.GetString(11)),
    };

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
