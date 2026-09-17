using System.Globalization;

namespace KineticTrainer.Persistence;

public sealed record FtpEntry(long Id, long? ProfileId, DateTimeOffset EffectiveAt, int Ftp, string Source, string? Note);

public interface IFtpHistoryRepository
{
    Task<long> AddAsync(long profileId, int ftp, string source, DateTimeOffset? effectiveAt = null, string? note = null, CancellationToken ct = default);

    /// <summary>Oldest first (chart order).</summary>
    Task<IReadOnlyList<FtpEntry>> ListAsync(long profileId, CancellationToken ct = default);

    /// <summary>The FTP in force for a profile at a moment (latest entry not after it), or null.</summary>
    Task<int?> FtpAtAsync(long profileId, DateTimeOffset moment, CancellationToken ct = default);
}

/// <summary>Every FTP change is appended, so trends can plot FTP over time and old rides keep their FTP.</summary>
public sealed class FtpHistoryRepository : IFtpHistoryRepository
{
    private readonly LocalDatabase _db;

    public FtpHistoryRepository(LocalDatabase db)
    {
        _db = db;
    }

    public async Task<long> AddAsync(long profileId, int ftp, string source, DateTimeOffset? effectiveAt = null, string? note = null, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ftp_history (profile_id, effective_utc, ftp, source, note) VALUES ($profile, $at, $ftp, $source, $note);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$profile", profileId);
        cmd.Parameters.AddWithValue("$at", (effectiveAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$ftp", ftp);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<FtpEntry>> ListAsync(long profileId, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, profile_id, effective_utc, ftp, source, note FROM ftp_history WHERE profile_id = $profile ORDER BY effective_utc, id";
        cmd.Parameters.AddWithValue("$profile", profileId);
        var list = new List<FtpEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new FtpEntry(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1),
                DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                (int)r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }
        return list;
    }

    public async Task<int?> FtpAtAsync(long profileId, DateTimeOffset moment, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ftp FROM ftp_history WHERE profile_id = $profile AND effective_utc <= $at ORDER BY effective_utc DESC, id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$profile", profileId);
        cmd.Parameters.AddWithValue("$at", moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }
}
