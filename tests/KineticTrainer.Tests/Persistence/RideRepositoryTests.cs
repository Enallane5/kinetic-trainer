using KineticTrainer.Core.Settings;
using KineticTrainer.Core.Statistics;
using KineticTrainer.Persistence;
using KineticTrainer.Tests.Recording;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KineticTrainer.Tests.Persistence;

public class RideRepositoryTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "kt-rides-" + Guid.NewGuid().ToString("N") + ".db");
    private LocalDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = new LocalDatabase(_path);
        await _db.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Save_list_get_delete_round_trip()
    {
        var repo = new RideRepository(_db);
        var profile = await new ProfileRepository(_db).EnsureDefaultAsync(null);
        var ride = RideFixtures.Ride(90);
        var id = await repo.SaveAsync(profile.Id, ride, "FitnessMachine", @"C:\rides\a.fit", @"C:\rides\a.tcx");
        var older = RideFixtures.Ride(30, simulated: true) with { StartedAt = RideFixtures.Start.AddDays(-1), StoppedAt = RideFixtures.Start.AddDays(-1).AddSeconds(30) };
        await repo.SaveAsync(profile.Id, older, "FitnessMachine", null, null);

        var list = await repo.ListAsync(profile.Id);
        Assert.Equal(2, list.Count);
        Assert.Equal(id, list[0].Id); // newest first
        Assert.Equal(ride.StartedAt, list[0].StartedAt);
        Assert.Equal(ride.Summary.DistanceMeters, list[0].DistanceMeters, 6);
        Assert.Equal(ride.Summary.AveragePowerWatts!.Value, list[0].AveragePowerWatts!.Value, 6);
        Assert.Equal(300, list[0].MaxPowerWatts);
        Assert.Equal(ride.Summary.NormalizedPowerWatts!.Value, list[0].NormalizedPowerWatts!.Value, 6);
        Assert.Equal("inRide 48343", list[0].SensorName);
        Assert.Equal(@"C:\rides\a.fit", list[0].FitPath);
        Assert.True(list[1].Simulated);

        var stored = await repo.GetAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(ride.Samples.Count, stored!.Samples.Count);
        Assert.Equal(ride.Summary.ElapsedSeconds, stored.Summary.ElapsedSeconds, 9);
        Assert.Equal(ride.Summary.NormalizedPowerWatts, stored.Summary.NormalizedPowerWatts);
        Assert.Equal(ride.Summary.PowerBests!.Count, stored.Summary.PowerBests!.Count);
        Assert.Equal(ride.Summary.PowerBests[60], stored.Summary.PowerBests[60], 9);
        Assert.Equal(profile.Id, stored.Item.ProfileId);
        for (var i = 0; i < ride.Samples.Count; i += 37)
        {
            Assert.Equal(ride.Samples[i].Timestamp, stored.Samples[i].Timestamp);
            Assert.Equal(ride.Samples[i].PowerWatts, stored.Samples[i].PowerWatts);
            Assert.Equal(ride.Samples[i].CadenceRpm, stored.Samples[i].CadenceRpm);
            Assert.Equal(ride.Samples[i].SpeedMetersPerSecond, stored.Samples[i].SpeedMetersPerSecond);
            Assert.Equal(ride.Samples[i].DistanceMeters, stored.Samples[i].DistanceMeters, 9);
        }

        await repo.DeleteAsync(id);
        Assert.Null(await repo.GetAsync(id));
        Assert.Single(await repo.ListAsync(profile.Id));
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ride_samples WHERE ride_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Calibrations_are_recorded_newest_first()
    {
        var repo = new RideRepository(_db);
        await repo.RecordCalibrationAsync(new CalibrationRecord(0, RideFixtures.Start, "inRide 48343", "FitnessMachine", false, "timed out", 9.7, 10.0));
        await repo.RecordCalibrationAsync(new CalibrationRecord(0, RideFixtures.Start.AddMinutes(5), "inRide 48343", "FitnessMachine", true, "ok", 9.7, 10.0));
        var list = await repo.ListCalibrationsAsync();
        Assert.Equal(2, list.Count);
        Assert.True(list[0].Succeeded);
        Assert.Equal(10.0, list[0].TargetHighMps);
        Assert.Equal("timed out", list[1].Result);
    }
}

public class MigrationTests
{
    [Fact]
    public async Task A_v1_database_upgrades_to_v2_without_losing_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), "kt-migrate-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var db = new LocalDatabase(path);
            await db.InitializeAsync(1, CancellationToken.None);
            Assert.Equal(1, await db.GetSchemaVersionAsync());
            var store = new SettingsStore(db);
            await store.SaveAsync(new AppSettings { SimulatorBridgePort = 50123 });

            await db.InitializeAsync();
            Assert.Equal(LocalDatabase.LatestSchemaVersion, await db.GetSchemaVersionAsync());
            Assert.Equal(50123, (await store.LoadAsync()).SimulatorBridgePort);
            var repo = new RideRepository(db);
            var profile = await new ProfileRepository(db).EnsureDefaultAsync(null);
            Assert.Empty(await repo.ListAsync(profile.Id));
            await repo.SaveAsync(profile.Id, RideFixtures.Ride(10), null, null, null);
            Assert.Single(await repo.ListAsync(profile.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}

public class DownsamplerTests
{
    [Fact]
    public void Keeps_peaks_and_troughs_per_bucket_in_x_order()
    {
        var pts = Enumerable.Range(0, 1000).Select(i => new SeriesDownsampler.Point(i, i % 50 == 25 ? 500 : i % 50 == 0 ? -5 : 100)).ToList();
        var ds = SeriesDownsampler.MinMax(pts, 20);
        Assert.True(ds.Count <= 40);
        Assert.Equal(500, ds.Max(p => p.Y));
        Assert.Equal(-5, ds.Min(p => p.Y));
        Assert.True(ds.Zip(ds.Skip(1)).All(p => p.First.X <= p.Second.X));
        Assert.Same(pts, SeriesDownsampler.MinMax(pts, 1000)); // short enough: unchanged
    }

    [Fact]
    public void Rolling_average_uses_the_trailing_window()
    {
        var s = new List<double> { 100, 200, 300, 400 };
        Assert.Equal(350, SeriesDownsampler.RollingAverage(s, 2));
        Assert.Equal(250, SeriesDownsampler.RollingAverage(s, 10));
        Assert.Null(SeriesDownsampler.RollingAverage([], 3));
    }
}
