using System.Text.Json;
using System.Text.Json.Serialization;
using KineticTrainer.Core.Settings;

namespace KineticTrainer.Persistence;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

/// <summary>Stores <see cref="AppSettings"/> as one JSON document in the settings table.</summary>
public sealed class SettingsStore : ISettingsStore
{
    private const string Key = "app";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LocalDatabase _db;

    public SettingsStore(LocalDatabase db)
    {
        _db = db;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", Key);
        var json = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppSettings();
        }
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Unreadable settings must not stop the app from starting; fall back to defaults.
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value, updated_utc) VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", Key);
        cmd.Parameters.AddWithValue("$value", json);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
