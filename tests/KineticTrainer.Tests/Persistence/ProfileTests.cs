using KineticTrainer.Core.Profiles;
using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Persistence;
using KineticTrainer.Recording;
using KineticTrainer.Tests.Recording;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KineticTrainer.Tests.Persistence;

public class ProfileTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "kt-profiles-" + Guid.NewGuid().ToString("N") + ".db");
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

    private static RideRecording Ride(DateTimeOffset start, Func<int, int> power, int seconds, bool simulated = false)
    {
        var hub = new TelemetryHub();
        using var recorder = new RideRecorder(hub);
        recorder.Start(start, 250);
        for (var i = 0; i < seconds; i++)
        {
            recorder.Observe(new TrainerTelemetry
            {
                Timestamp = start.AddSeconds(i), ConnectionState = TrainerConnectionState.Connected, IsDataValid = true,
                PowerWatts = power(i), CadenceRpm = 85 + i % 10, SpeedMetersPerSecond = 9, IsSimulated = simulated,
            });
        }
        return recorder.Stop(start.AddSeconds(seconds))!;
    }

    [Fact]
    public async Task Profiles_are_isolated_and_crud_works()
    {
        var profiles = new ProfileRepository(_db);
        var rides = new RideRepository(_db);
        var alice = await profiles.CreateAsync(new RiderProfile { Name = "Alice", Ftp = 230, WeightKg = 61.5, HeightCm = 168, BirthYear = 1990, Sex = RiderSex.Female });
        var bob = await profiles.CreateAsync(new RiderProfile { Name = "Bob Smith", Ftp = 280, WeightKg = 82, Color = RiderProfile.Palette[1] });
        Assert.Equal("BS", bob.Initials);
        Assert.Equal(230 / 61.5, alice.FtpWattsPerKg!.Value, 9);

        await rides.SaveAsync(alice.Id, Ride(RideFixtures.Start, _ => 200, 120), null, null, null);
        await rides.SaveAsync(bob.Id, Ride(RideFixtures.Start.AddHours(1), _ => 300, 120), null, null, null);
        Assert.Single(await rides.ListAsync(alice.Id));
        Assert.Single(await rides.ListAsync(bob.Id));

        await profiles.UpdateAsync(alice with { Ftp = 240, WeightKg = 60 });
        var reloaded = (await profiles.GetAsync(alice.Id))!;
        Assert.Equal(240, reloaded.Ftp);
        Assert.Equal(60, reloaded.WeightKg);
        Assert.Equal(RiderSex.Female, reloaded.Sex);
        var history = await new FtpHistoryRepository(_db).ListAsync(alice.Id);
        Assert.Equal([230, 240], history.Select(h => h.Ftp));

        await profiles.TouchAsync(bob.Id);
        Assert.Equal(bob.Id, (await profiles.ListAsync())[0].Id);

        await profiles.DeleteAsync(bob.Id);
        Assert.Null(await profiles.GetAsync(bob.Id));
        Assert.Empty(await rides.ListAsync(bob.Id));
        Assert.Single(await rides.ListAsync(alice.Id));
        Assert.Empty(await new FtpHistoryRepository(_db).ListAsync(bob.Id));
    }

    [Fact]
    public async Task Invalid_profiles_are_rejected()
    {
        var profiles = new ProfileRepository(_db);
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.CreateAsync(new RiderProfile { Name = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.CreateAsync(new RiderProfile { Name = "X", Ftp = 5 }));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.CreateAsync(new RiderProfile { Name = "X", WeightKg = 10 }));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.CreateAsync(new RiderProfile { Name = "X", HeightCm = 300 }));
    }

    [Fact]
    public async Task Default_profile_adopts_rides_recorded_before_profiles_existed()
    {
        // Simulate a pre-profile ride row.
        await using (var conn = _db.Open())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO rides (started_utc, stopped_utc, elapsed_s, moving_s, distance_m, sample_count, simulated, created_utc)
                VALUES ('2026-09-16T22:19:02Z', '2026-09-16T22:21:10Z', 128, 120, 710, 254, 0, '2026-09-16T22:21:10Z')
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var profiles = new ProfileRepository(_db);
        var p = await profiles.EnsureDefaultAsync(preferredId: 12345);
        Assert.Equal("Rider 1", p.Name);
        Assert.Single(await new RideRepository(_db).ListAsync(p.Id));
        var again = await profiles.EnsureDefaultAsync(p.Id);
        Assert.Equal(p.Id, again.Id);
        Assert.Single(await profiles.ListAsync());
    }

    [Fact]
    public async Task Records_track_max_power_and_best_efforts_across_rides_ignoring_simulated()
    {
        var profiles = new ProfileRepository(_db);
        var rides = new RideRepository(_db);
        var p = await profiles.CreateAsync(new RiderProfile { Name = "Racer" });
        // Ride 1: 5 s sprint at 900 W inside 200 W.
        await rides.SaveAsync(p.Id, Ride(RideFixtures.Start, i => i is >= 30 and < 35 ? 900 : 200, 600), null, null, null);
        // Ride 2: steady 260 W for 25 min (better 20 min), lower sprint.
        await rides.SaveAsync(p.Id, Ride(RideFixtures.Start.AddDays(2), i => i is >= 10 and < 15 ? 700 : 260, 1500), null, null, null);
        // Simulated ride with absurd power must not count.
        await rides.SaveAsync(p.Id, Ride(RideFixtures.Start.AddDays(3), _ => 1500, 120, simulated: true), null, null, null);

        var rec = await profiles.GetRecordsAsync(p.Id);
        Assert.Equal(2, rec.RideCount);
        Assert.Equal(900, rec.MaxPowerWatts);
        Assert.Equal(RideFixtures.Start, rec.MaxPowerDate);
        Assert.Equal(2100, rec.TotalSeconds, 6);
        var b5 = rec.PowerBests.Single(b => b.DurationSeconds == 5);
        Assert.Equal(900, b5.Watts, 6);
        var b20 = rec.PowerBests.Single(b => b.DurationSeconds == 1200);
        Assert.True(b20.Watts > 259 && b20.Watts < 262, $"{b20.Watts}");
        Assert.Equal(RideFixtures.Start.AddDays(2), b20.RideDate);
        Assert.DoesNotContain(rec.PowerBests, b => b.DurationSeconds == 3600);
        Assert.Equal(ProfileRecords.Empty.RideCount, (await profiles.GetRecordsAsync(p.Id + 100)).RideCount);
    }
}

public class PowerCurveTests
{
    [Fact]
    public void Best_average_uses_sliding_window()
    {
        Assert.Equal(350, PowerCurve.BestAverage([100, 200, 300, 400, 100], 2)!.Value); // 300+400
        Assert.Equal(220, PowerCurve.BestAverage([100, 200, 300, 400, 100], 5)!.Value);
        Assert.Null(PowerCurve.BestAverage([100], 2));
    }

    [Fact]
    public void Efforts_never_span_gaps()
    {
        var series = new List<RideStatisticsCalculator.SecondSample>();
        for (var i = 0; i < 10; i++) series.Add(new(i, 400, null, null));
        for (var i = 20; i < 30; i++) series.Add(new(i, 400, null, null));
        var curve = PowerCurve.Compute(series, [5, 15]);
        Assert.Equal(400, curve[5]);
        Assert.False(curve.ContainsKey(15)); // two 10 s pieces cannot make a 15 s effort
        Assert.Equal("20 min", PowerCurve.Label(1200));
        Assert.Equal("5 s", PowerCurve.Label(5));
    }
}
