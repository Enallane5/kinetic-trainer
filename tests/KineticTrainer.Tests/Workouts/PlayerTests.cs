using KineticTrainer.Persistence;
using KineticTrainer.Workouts.Library;
using KineticTrainer.Workouts.Model;
using KineticTrainer.Workouts.Playback;
using KineticTrainer.Workouts.Testing;
using KineticTrainer.Workouts.Timeline;
using KineticTrainer.Workouts.Zwo;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KineticTrainer.Tests.Workouts;

/// <summary>Drives a player with a fake clock at a fixed update rate.</summary>
internal sealed class PlayerHarness
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);

    public PlayerHarness(Workout workout, int? ftp, PlayerOptions? options = null)
    {
        Player = new WorkoutPlayer(workout, ftp, options);
        Player.CueRaised += c => Cues.Add(c);
    }

    public WorkoutPlayer Player { get; }
    public List<WorkoutCue> Cues { get; } = [];
    public DateTimeOffset Now { get; private set; } = T0;
    public PlayerSnapshot? Last { get; private set; }

    public void Start() => Last = Player.Start(Now);

    /// <summary>Advances <paramref name="seconds"/> in 0.25 s steps, supplying power from a function of workout time.</summary>
    public void Ride(double seconds, Func<double, double?> power)
    {
        var steps = (int)Math.Round(seconds / 0.25);
        for (var i = 0; i < steps; i++)
        {
            var p = power(Player.ElapsedSeconds);
            Now = Now.AddSeconds(0.25);
            Last = Player.Update(Now, new PlayerInput(p, p, 90));
        }
    }

    public void Wait(double seconds) => Now = Now.AddSeconds(seconds);
}

public class WorkoutPlayerTests
{
    private static Workout TwoBlocks => new()
    {
        Name = "Two blocks",
        Steps =
        [
            WorkoutStep.Steady(120, 0.5) with { TextEvents = [new(0, "hello"), new(60, "halfway")] },
            WorkoutStep.Steady(60, 1.0),
            WorkoutStep.Steady(30, 0.52),
        ],
    };

    [Fact]
    public void Raises_start_text_warning_countdown_and_transition_cues_once()
    {
        var h = new PlayerHarness(TwoBlocks, 200);
        h.Start();
        Assert.Equal(PlayerState.Running, h.Player.State);
        Assert.Equal(["hello", "Warm-up: 100 W for 2 minutes"], h.Cues.Select(c => c.Message));
        h.Cues.Clear();

        h.Ride(125, _ => 100);
        Assert.Equal(
            [
                (CueKind.Text, "halfway"),
                (CueKind.Upcoming, "Next: Effort, 200 W for 1 minute in 30 seconds"),
                (CueKind.Upcoming, "Effort in 10 seconds"),
                (CueKind.Countdown, "3"),
                (CueKind.Countdown, "2"),
                (CueKind.Countdown, "1"),
                (CueKind.SegmentStart, "Effort: 200 W for 1 minute"),
            ],
            h.Cues.Select(c => (c.Kind, c.Message)));
        Assert.Equal([60, 90, 110, 117, 118, 119, 120], h.Cues.Select(c => c.AtSecond));
        Assert.Equal(200, h.Last!.TargetWatts);
        Assert.Equal(TargetState.Under, h.Last.TargetState);
        Assert.Equal(55, h.Last.SegmentRemainingSeconds, 6);
        Assert.Equal(85, h.Last.TotalRemainingSeconds, 6);
        Assert.Equal("104 W", h.Last.NextTargetText);
        h.Cues.Clear();

        h.Ride(100, _ => 200);
        Assert.Equal(PlayerState.Completed, h.Player.State);
        // 60 s block → no 30 s warning; 10 s warning, countdown, recovery; then the end countdown and complete.
        Assert.Equal(
            [CueKind.Upcoming, CueKind.Countdown, CueKind.Countdown, CueKind.Countdown, CueKind.Cooldown,
             CueKind.Countdown, CueKind.Countdown, CueKind.Countdown, CueKind.Complete],
            h.Cues.Select(c => c.Kind));
        Assert.Equal(210, h.Player.ElapsedSeconds);
    }

    [Fact]
    public void Scores_compliance_with_a_minimum_band()
    {
        var h = new PlayerHarness(TwoBlocks, 200, new PlayerOptions { TolerancePercent = 1, MinimumBandWatts = 5 });
        h.Start();
        h.Ride(60, _ => 104);      // 100 W target, band max(5, 1) = 5 → on target
        h.Ride(60, _ => 94);       // under
        h.Ride(30, _ => 215);      // 200 W target → over
        h.Ride(30, _ => null);     // no data
        h.Ride(30, _ => 104);      // recovery 104 W target → on
        var c = h.Player.BuildCompliance();
        Assert.True(c.Completed);
        Assert.Equal(3, c.Segments.Count);
        Assert.Equal((60d, 60d, 0d, 0d), (c.Segments[0].OnTargetSeconds, c.Segments[0].UnderSeconds, c.Segments[0].OverSeconds, c.Segments[0].NoDataSeconds));
        Assert.Equal((0d, 30d, 30d), (c.Segments[1].OnTargetSeconds, c.Segments[1].OverSeconds, c.Segments[1].NoDataSeconds));
        Assert.Equal(200, c.Segments[1].TargetWatts);
        Assert.Equal(215, c.Segments[1].AverageWatts);
        Assert.Equal(Math.Round(90.0 / 180 * 100, 1), c.OnTargetPercent);
        Assert.Equal(210, c.RiddenSeconds);

        var json = c.ToJson();
        Assert.Contains("\"role\":\"Warmup\"", json);
        var back = WorkoutCompliance.FromJson(json)!;
        Assert.Equal(c.OnTargetPercent, back.OnTargetPercent);
        Assert.Equal(c.Segments, back.Segments);
        Assert.Null(WorkoutCompliance.FromJson("{broken"));
        Assert.Equal(210, back.PowerByWorkoutSecond!.Count);
        Assert.Equal(104, back.PowerByWorkoutSecond[10]);
        Assert.Equal(-1, back.PowerByWorkoutSecond[165]);
    }

    [Fact]
    public void Pause_stops_the_clock_and_skip_jumps_without_replaying_cues()
    {
        var h = new PlayerHarness(TwoBlocks, 200);
        h.Start();
        h.Ride(10, _ => 100);
        h.Player.Pause(h.Now);
        h.Wait(300);
        h.Ride(5, _ => 100);
        Assert.Equal(10, h.Player.ElapsedSeconds, 6);
        Assert.Equal(PlayerState.Paused, h.Last!.State);
        h.Player.Resume(h.Now);
        h.Ride(5, _ => 100);
        Assert.Equal(15, h.Player.ElapsedSeconds, 6);

        h.Cues.Clear();
        h.Player.Skip(h.Now);
        Assert.Equal(120, h.Player.ElapsedSeconds);
        Assert.Equal([CueKind.SegmentStart], h.Cues.Select(c => c.Kind)); // no "halfway", no countdown
        h.Player.SetIntensity(110);
        Assert.Equal(220, h.Player.Snapshot(null).TargetWatts);
        h.Player.SetIntensity(500);
        Assert.Equal(150, h.Player.IntensityPercent);

        h.Player.Skip(h.Now);
        h.Player.Skip(h.Now);
        Assert.Equal(PlayerState.Completed, h.Player.State);
        var c = h.Player.BuildCompliance();
        Assert.True(c.Segments[0].Skipped);
        Assert.True(c.Segments[1].Skipped);
        Assert.Equal(15, c.RiddenSeconds);
    }

    [Fact]
    public void Large_clock_jumps_are_capped()
    {
        var h = new PlayerHarness(TwoBlocks, 200);
        h.Start();
        h.Wait(3600);
        h.Player.Update(h.Now, default);
        Assert.Equal(10, h.Player.ElapsedSeconds, 6);
    }

    [Fact]
    public void Missing_ftp_gives_percent_targets_and_no_scoring()
    {
        var h = new PlayerHarness(TwoBlocks, null);
        h.Start();
        h.Ride(1, _ => 150);
        Assert.Null(h.Last!.TargetWatts);
        Assert.Equal("50% FTP", h.Last.TargetText);
        Assert.Equal(TargetState.NoTarget, h.Last.TargetState);
    }
}

public class FtpTestTests
{
    private static WorkoutEntry Test(FtpTestKind kind) => WorkoutLibrary.FtpTests.Single(e => e.Workout.FtpTest == kind);

    [Fact]
    public void Twenty_minute_test_is_95_percent_of_the_effort()
    {
        var entry = Test(FtpTestKind.TwentyMinute);
        var effort = entry.Timeline.Segments.Single(s => s.DurationSeconds == 1200);
        var h = new PlayerHarness(entry.Workout, 240);
        h.Start();
        h.Ride(entry.Timeline.TotalSeconds, t => t >= effort.StartSecond && t < effort.EndSecond ? 262 : 150);
        Assert.Equal(PlayerState.Completed, h.Player.State);
        var estimate = h.Player.EstimateFtp()!;
        Assert.True(estimate.IsValid, string.Join("; ", estimate.Warnings));
        Assert.Equal(249, estimate.Ftp); // 262 × 0.95 = 248.9
        Assert.Equal(262, estimate.BasisWatts!.Value, 6);
        Assert.Empty(estimate.Warnings);
        Assert.Equal(249, h.Player.BuildCompliance(estimate).EstimatedFtp);
    }

    [Fact]
    public void Twenty_minute_test_without_enough_data_gives_no_estimate()
    {
        var entry = Test(FtpTestKind.TwentyMinute);
        var effort = entry.Timeline.Segments.Single(s => s.DurationSeconds == 1200);
        var power = new double?[entry.Timeline.TotalSeconds];
        for (var s = effort.StartSecond; s < effort.EndSecond; s++)
        {
            power[s] = (s - effort.StartSecond) % 5 == 0 ? null : 300; // 80 % coverage
        }
        var e = FtpEstimator.Estimate(entry.Timeline, power, 250);
        Assert.False(e.IsValid);

        for (var s = effort.StartSecond; s < effort.StartSecond + 60; s++)
        {
            power[s] = null;
        }
        for (var s = effort.StartSecond + 60; s < effort.EndSecond; s++)
        {
            power[s] = 400;
        }
        e = FtpEstimator.Estimate(entry.Timeline, power, 250);
        Assert.True(e.IsValid);
        Assert.Equal(380, e.Ftp);
        Assert.Contains(e.Warnings, w => w.Contains("60 s without power"));
        Assert.Contains(e.Warnings, w => w.Contains("rise of 52%"));
    }

    [Fact]
    public void Ramp_test_ends_on_failure_and_uses_best_minute()
    {
        var entry = Test(FtpTestKind.Ramp);
        const int ftp = 250;
        var h = new PlayerHarness(entry.Workout, ftp);
        var (rampStart, rampEnd) = h.Player.RampWindow!.Value;
        Assert.Equal((300, 1440), (rampStart, rampEnd));
        h.Start();
        // Follow the target until 400 W (step at 1.60 × 250 = 400 W is never reached), then collapse.
        h.Ride(1500, t =>
        {
            var target = h.Player.Timeline.TargetWattsAt(t, ftp) ?? 0;
            return target <= 340 ? target : 60;
        });
        Assert.NotNull(h.Player.RampEndedAtSecond);
        var ended = h.Player.RampEndedAtSecond!.Value;
        // 340 W = 1.36 × 250 is step 15 (starting at 300 + 14 × 60 = 1140 s); the next step starts at 1200 and
        // failure is declared after 10 s below half the target.
        Assert.InRange(ended, 1209, 1212);
        Assert.Contains(h.Cues, c => c.Message.StartsWith("Ramp complete"));
        Assert.True(h.Player.ElapsedSeconds >= rampEnd);
        var estimate = h.Player.EstimateFtp()!;
        Assert.True(estimate.IsValid, string.Join("; ", estimate.Warnings));
        Assert.Equal(255, estimate.Ftp); // 0.75 × 340
        var compliance = h.Player.BuildCompliance(estimate);
        Assert.Equal(ended, compliance.RampEndedAtSecond);
        Assert.Contains(compliance.Segments, s => s.Skipped);
    }

    [Fact]
    public void Ramp_can_be_ended_by_the_rider_and_dropouts_do_not_end_it()
    {
        var entry = Test(FtpTestKind.Ramp);
        var h = new PlayerHarness(entry.Workout, 200);
        h.Start();
        h.Ride(400, _ => 150);
        h.Ride(60, _ => null); // sensor drop-out: not a failure
        Assert.Null(h.Player.RampEndedAtSecond);
        Assert.True(h.Last!.CanEndRamp);
        h.Player.EndRamp(h.Now);
        Assert.Equal(460, h.Player.RampEndedAtSecond);
        Assert.False(h.Player.InRamp);
        var e = h.Player.EstimateFtp()!;
        Assert.Equal(113, e.Ftp); // 0.75 × 150
        Assert.Contains(e.Warnings, w => w.Contains("within 4 minutes"));
    }

    [Fact]
    public void Eight_minute_test_uses_the_better_effort()
    {
        var entry = Test(FtpTestKind.EightMinute);
        var efforts = entry.Timeline.Segments.Where(s => s.DurationSeconds == 480).ToList();
        Assert.Equal(2, efforts.Count);
        var power = new double?[entry.Timeline.TotalSeconds];
        for (var s = 0; s < power.Length; s++)
        {
            power[s] = s >= efforts[0].StartSecond && s < efforts[0].EndSecond ? 300
                : s >= efforts[1].StartSecond && s < efforts[1].EndSecond ? 320 : 120;
        }
        var e = FtpEstimator.Estimate(entry.Timeline, power, 290);
        Assert.Equal(288, e.Ftp); // 0.9 × 320
        Assert.Empty(e.Warnings);
    }

    [Fact]
    public void Implausible_results_and_non_tests_are_rejected()
    {
        var entry = Test(FtpTestKind.TwentyMinute);
        var power = Enumerable.Repeat<double?>(20, entry.Timeline.TotalSeconds).ToArray();
        var e = FtpEstimator.Estimate(entry.Timeline, power, null);
        Assert.False(e.IsValid);
        Assert.Contains(e.Warnings, w => w.Contains("plausible range"));

        var plain = WorkoutLibrary.BuiltIn.First(x => x.Workout.FtpTest == FtpTestKind.None);
        Assert.Null(new WorkoutPlayer(plain.Workout, 200).EstimateFtp());
    }
}

public class WorkoutRepositoryTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "kt-workouts-" + Guid.NewGuid().ToString("N") + ".db");
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
    public async Task Crud_and_profile_scoping()
    {
        var profiles = new ProfileRepository(_db);
        var a = await profiles.CreateAsync(new KineticTrainer.Core.Profiles.RiderProfile { Name = "A" });
        var b = await profiles.CreateAsync(new KineticTrainer.Core.Profiles.RiderProfile { Name = "B" });
        var repo = new WorkoutRepository(_db);
        var zwo = ZwoWriter.Write(new Workout { Name = "Mine", Steps = [WorkoutStep.Steady(60, 0.8)] });
        var mine = await repo.AddAsync(a.Id, "Mine", "Custom", WorkoutRepository.SourceCustom, zwo);
        var shared = await repo.AddAsync(null, "Shared", null, WorkoutRepository.SourceImported, zwo);
        Assert.Equal(2, (await repo.ListAsync(a.Id)).Count);
        Assert.Equal([shared], (await repo.ListAsync(b.Id)).Select(w => w.Id));

        await repo.UpdateAsync(mine, "Renamed", "Tempo", zwo);
        var got = (await repo.GetAsync(mine))!;
        Assert.Equal(("Renamed", "Tempo", "custom", a.Id), (got.Name, got.Category, got.Source, got.ProfileId));
        Assert.True(ZwoParser.Parse(got.Zwo).Success);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.AddAsync(a.Id, " ", null, "custom", zwo));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpdateAsync(9999, "x", null, zwo));

        await profiles.DeleteAsync(a.Id);
        Assert.Null(await repo.GetAsync(mine));
        await repo.DeleteAsync(shared);
        Assert.Empty(await repo.ListAsync(b.Id));
    }
}
