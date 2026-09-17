using Microsoft.Data.Sqlite;

namespace KineticTrainer.Persistence;

/// <summary>
/// Opens the local SQLite file and applies schema migrations in order. Each migration is a numbered SQL script;
/// a future version adds a new entry and never edits an old one, so existing databases upgrade in place.
/// </summary>
public sealed class LocalDatabase
{
    /// <summary>Ordered migrations. Index + 1 is the schema version after applying it.</summary>
    private static readonly string[] Migrations =
    [
        // v1: settings key/value store (JSON documents) - Phase 1
        """
        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """,
        // v2: ride history with per-sample data, and spin-down calibration log - Phase 3
        """
        CREATE TABLE IF NOT EXISTS rides (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            started_utc TEXT NOT NULL,
            stopped_utc TEXT NOT NULL,
            elapsed_s REAL NOT NULL,
            moving_s REAL NOT NULL,
            distance_m REAL NOT NULL,
            sample_count INTEGER NOT NULL,
            avg_power REAL,
            max_power INTEGER,
            avg_cadence REAL,
            max_cadence REAL,
            avg_speed REAL,
            max_speed REAL,
            normalized_power REAL,
            work_kj REAL,
            sensor_name TEXT,
            protocol TEXT,
            simulated INTEGER NOT NULL DEFAULT 0,
            fit_path TEXT,
            tcx_path TEXT,
            notes TEXT,
            created_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_rides_started ON rides(started_utc);
        CREATE TABLE IF NOT EXISTS ride_samples (
            ride_id INTEGER NOT NULL,
            seq INTEGER NOT NULL,
            timestamp_utc TEXT NOT NULL,
            elapsed_s REAL NOT NULL,
            power INTEGER,
            cadence REAL,
            speed REAL,
            distance_m REAL NOT NULL,
            PRIMARY KEY (ride_id, seq)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS calibrations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            performed_utc TEXT NOT NULL,
            sensor_name TEXT,
            protocol TEXT,
            succeeded INTEGER NOT NULL,
            result TEXT,
            target_low_mps REAL,
            target_high_mps REAL
        );
        """,
        // v3: FTP and zones - Phase 4
        """
        ALTER TABLE rides ADD COLUMN ftp INTEGER;
        ALTER TABLE rides ADD COLUMN zone_seconds TEXT;
        ALTER TABLE rides ADD COLUMN intensity_factor REAL;
        ALTER TABLE rides ADD COLUMN tss REAL;
        ALTER TABLE rides ADD COLUMN title TEXT;
        CREATE TABLE IF NOT EXISTS ftp_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            effective_utc TEXT NOT NULL,
            ftp INTEGER NOT NULL,
            source TEXT NOT NULL,
            note TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_ftp_history_effective ON ftp_history(effective_utc);
        """,
        // v4: rider profiles, per-ride power bests, workouts - Phases 4-6
        """
        CREATE TABLE IF NOT EXISTS profiles (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            ftp INTEGER,
            weight_kg REAL,
            height_cm REAL,
            birth_year INTEGER,
            sex TEXT,
            max_hr INTEGER,
            resting_hr INTEGER,
            color TEXT,
            created_utc TEXT NOT NULL,
            last_used_utc TEXT
        );
        ALTER TABLE rides ADD COLUMN profile_id INTEGER;
        ALTER TABLE rides ADD COLUMN workout_zwo TEXT;
        ALTER TABLE rides ADD COLUMN compliance_json TEXT;
        ALTER TABLE ftp_history ADD COLUMN profile_id INTEGER;
        CREATE INDEX IF NOT EXISTS ix_rides_profile ON rides(profile_id, started_utc);
        CREATE TABLE IF NOT EXISTS ride_power_bests (
            ride_id INTEGER NOT NULL,
            duration_s INTEGER NOT NULL,
            watts REAL NOT NULL,
            PRIMARY KEY (ride_id, duration_s)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS workouts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_id INTEGER,
            name TEXT NOT NULL,
            category TEXT,
            source TEXT NOT NULL,
            zwo TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """,
        // v5: workout lookup index; optional online services (Strava) - linked accounts and per-ride upload status.
        // Tokens are stored as an opaque blob that the app encrypts for the current Windows user (DPAPI).
        """
        CREATE INDEX IF NOT EXISTS ix_workouts_profile ON workouts(profile_id);
        CREATE TABLE IF NOT EXISTS linked_accounts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_id INTEGER NOT NULL,
            service TEXT NOT NULL,
            account_id TEXT,
            account_name TEXT,
            token_blob BLOB,
            expires_utc TEXT,
            scope TEXT,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            UNIQUE (profile_id, service)
        );
        CREATE TABLE IF NOT EXISTS ride_uploads (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ride_id INTEGER NOT NULL,
            service TEXT NOT NULL,
            status TEXT NOT NULL,
            upload_id TEXT,
            external_id TEXT,
            error TEXT,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            UNIQUE (ride_id, service)
        );
        """,
    ];

    public LocalDatabase(string path)
    {
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    public string ConnectionString { get; }

    public static int LatestSchemaVersion => Migrations.Length;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KineticTrainer", "kinetic-trainer.db");

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    public Task InitializeAsync(CancellationToken ct = default) => InitializeAsync(null, ct);

    /// <summary>Applies migrations up to <paramref name="targetVersion"/> (null = latest). Tests use this to simulate old databases.</summary>
    internal async Task InitializeAsync(int? targetVersion, CancellationToken ct)
    {
        var target = Math.Min(targetVersion ?? Migrations.Length, Migrations.Length);
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var conn = Open();
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var current = await GetSchemaVersionAsync(conn, ct).ConfigureAwait(false);
        for (var v = current; v < target; v++)
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = Migrations[v];
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = $"PRAGMA user_version = {v + 1};";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        return await GetSchemaVersionAsync(conn, ct).ConfigureAwait(false);
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }
}
