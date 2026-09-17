using KineticTrainer.Core.Rides;
using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Zones;
using KineticTrainer.Persistence;
using KineticTrainer.Tests.Recording;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KineticTrainer.Tests.Core;

public class PowerZoneTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(110, 1)]   // 55 % of 200
    [InlineData(111, 2)]
    [InlineData(150, 2)]   // 75 %
    [InlineData(151, 3)]
    [InlineData(180, 3)]   // 90 %
    [InlineData(181, 4)]
    [InlineData(210, 4)]   // 105 %
    [InlineData(211, 5)]
    [InlineData(240, 5)]   // 120 %
    [InlineData(241, 6)]
    [InlineData(300, 6)]   // 150 %
    [InlineData(301, 7)]
    [InlineData(1500, 7)]
    public void Coggan_boundaries_are_upper_inclusive(int watts, int zone)
    {
        Assert.Equal(zone, PowerZones.ZoneFor(watts, 200)!.Number);
    }

    [Fact]
    public void Unknown_ftp_or_power_gives_no_zone()
    {
        Assert.Null(PowerZones.ZoneFor(200, null));
        Assert.Null(PowerZones.ZoneFor(200, 10));
        Assert.Null(PowerZones.ZoneFor(null, 250));
        Assert.Null(PowerZones.ZoneFor(-3, 250));
        Assert.Null(PowerZones.PercentOfFtp(100, null));
        Assert.Equal(40, PowerZones.PercentOfFtp(100, 250));
    }

    [Fact]
    public void Watt_ranges_are_contiguous_and_colours_are_valid_hex()
    {
        var ftp = 250;
        var previousHigh = -1;
        foreach (var z in PowerZones.Coggan)
        {
            var (low, high) = z.WattRange(ftp);
            Assert.Equal(previousHigh + 1, low);
            previousHigh = high ?? int.MaxValue - 1;
            Assert.Matches("^#[0-9A-F]{6}$", z.ColorHex);
        }
        Assert.Equal("0–137 W", PowerZones.Coggan[0].DescribeRange(ftp));
        Assert.Equal("376+ W", PowerZones.Coggan[6].DescribeRange(ftp));
        Assert.Equal(7, PowerZones.Coggan.Select(z => z.ColorHex).Distinct().Count());
    }

    [Fact]
    public void Time_in_zones_counts_seconds()
    {
        var seconds = PowerZones.TimeInZones([100, 100, 160, 200, 200, 200, 400], 200);
        Assert.Equal([2, 0, 1, 3, 0, 0, 1], seconds);
        Assert.All(PowerZones.TimeInZones([100], 0), s => Assert.Equal(0, s));
    }
}

public class IntensityTests
{
    private static RideSample S(double t, int p) => new(RideFixtures.Start.AddSeconds(t), t, p, 90, 8, t * 8);

    [Fact]
    public void One_hour_at_ftp_is_100_tss_and_if_1()
    {
        var samples = Enumerable.Range(0, 3600).Select(i => S(i, 250)).ToList();
        var s = RideStatisticsCalculator.Compute(samples, RideFixtures.Start, RideFixtures.Start.AddHours(1), ftp: 250);
        Assert.Equal(250, s.Ftp);
        Assert.Equal(1.0, s.IntensityFactor!.Value, 9);
        Assert.Equal(100.0, s.TrainingStressScore!.Value, 6);
        Assert.Equal(3600, s.ZoneSeconds![3]);
        Assert.Equal(3600, s.ZoneSeconds.Sum());
    }

    [Fact]
    public void Half_hour_at_75_percent_is_about_28_tss()
    {
        var samples = Enumerable.Range(0, 1800).Select(i => S(i, 150)).ToList();
        var s = RideStatisticsCalculator.Compute(samples, RideFixtures.Start, RideFixtures.Start.AddMinutes(30), ftp: 200);
        Assert.Equal(0.75, s.IntensityFactor!.Value, 9);
        Assert.Equal(0.5 * 0.75 * 0.75 * 100, s.TrainingStressScore!.Value, 6);
    }

    [Fact]
    public void Without_ftp_there_are_no_ftp_relative_figures()
    {
        var samples = Enumerable.Range(0, 120).Select(i => S(i, 150)).ToList();
        var s = RideStatisticsCalculator.Compute(samples, RideFixtures.Start, RideFixtures.Start.AddMinutes(2));
        Assert.Null(s.Ftp);
        Assert.Null(s.ZoneSeconds);
        Assert.Null(s.IntensityFactor);
        Assert.Null(s.TrainingStressScore);
    }
}

public class RollingWindowTests
{
    [Fact]
    public void Averages_over_trailing_windows_and_trims_to_span()
    {
        var w = new RollingWindow(TimeSpan.FromSeconds(60));
        var t0 = RideFixtures.Start;
        for (var i = 0; i < 20; i++)
        {
            w.Add(t0.AddMilliseconds(500 * i), i < 14 ? 100 : 200); // last 6 samples (3 s) at 200
        }
        var now = t0.AddMilliseconds(500 * 19);
        Assert.Equal(200, w.Average(TimeSpan.FromSeconds(2.5), now)); // samples at now-2.5..now
        Assert.Equal((14 - 0 + 0) * 0 + (100 * 14 + 200 * 6) / 20.0, w.Average(TimeSpan.FromSeconds(10), now));
        var snap = w.Snapshot(now);
        Assert.Equal(20, snap.Count);
        Assert.Equal(0, snap[^1].X, 9);
        Assert.Equal(-9.5, snap[0].X, 9);

        w.Add(t0.AddSeconds(120), 50);
        Assert.Equal(1, w.Count);
        Assert.Null(w.Average(TimeSpan.FromSeconds(3), t0));
    }
}

public class FtpHistoryTests
{
    [Fact]
    public async Task Entries_are_kept_in_order_and_ftp_at_a_moment_is_resolved()
    {
        var path = Path.Combine(Path.GetTempPath(), "kt-ftp-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var db = new LocalDatabase(path);
            await db.InitializeAsync();
            var repo = new FtpHistoryRepository(db);
            var profile = await new ProfileRepository(db).EnsureDefaultAsync(null);
            var t0 = RideFixtures.Start;
            await repo.AddAsync(profile.Id, 200, "manual", t0);
            await repo.AddAsync(profile.Id, 215, "manual", t0.AddDays(30), "after block 1");
            Assert.Null(await repo.FtpAtAsync(profile.Id, t0.AddDays(-1)));
            Assert.Equal(200, await repo.FtpAtAsync(profile.Id, t0.AddDays(1)));
            Assert.Equal(215, await repo.FtpAtAsync(profile.Id, t0.AddDays(31)));
            Assert.Null(await repo.FtpAtAsync(profile.Id + 99, t0.AddDays(31)));
            var list = await repo.ListAsync(profile.Id);
            Assert.Equal([200, 215], list.Select(e => e.Ftp));
            Assert.Equal("after block 1", list[1].Note);

            // Rides store their FTP-relative figures.
            var rides = new RideRepository(db);
            var hub = new KineticTrainer.Core.Telemetry.TelemetryHub();
            using var recorder = new KineticTrainer.Recording.RideRecorder(hub);
            recorder.Start(t0, ftp: 200);
            for (var i = 0; i < 120; i++)
            {
                recorder.Observe(new KineticTrainer.Core.Telemetry.TrainerTelemetry
                {
                    Timestamp = t0.AddSeconds(i), ConnectionState = KineticTrainer.Core.Telemetry.TrainerConnectionState.Connected,
                    IsDataValid = true, PowerWatts = 200, SpeedMetersPerSecond = 8, CadenceRpm = 90,
                });
            }
            var ride = recorder.Stop(t0.AddSeconds(120))! with { Title = "Test ride" };
            var id = await rides.SaveAsync(profile.Id, ride, "FitnessMachine", null, null);
            var stored = (await rides.GetAsync(id))!;
            Assert.Equal(200, stored.Summary.Ftp);
            Assert.Equal(120, stored.Summary.ZoneSeconds![3]);
            Assert.Equal(1.0, stored.Summary.IntensityFactor!.Value, 9);
            Assert.NotNull(stored.Summary.TrainingStressScore);
            Assert.Equal("Test ride", stored.Item.Title);
            Assert.Single(await rides.ListBetweenAsync(profile.Id, t0.AddMinutes(-1), t0.AddMinutes(1)));
            Assert.Empty(await rides.ListBetweenAsync(profile.Id, t0.AddMinutes(1), t0.AddMinutes(2)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
