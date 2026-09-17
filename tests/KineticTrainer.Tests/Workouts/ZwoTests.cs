using KineticTrainer.Workouts.Library;
using KineticTrainer.Workouts.Model;
using KineticTrainer.Workouts.Timeline;
using KineticTrainer.Workouts.Zwo;
using Xunit;

namespace KineticTrainer.Tests.Workouts;

public class ZwoParserTests
{
    private static Workout Ok(string xml)
    {
        var r = ZwoParser.Parse(xml, "fallback");
        Assert.True(r.Success, string.Join("; ", r.Issues));
        return r.Workout!;
    }

    private static ZwoParseResult Bad(string xml)
    {
        var r = ZwoParser.Parse(xml);
        Assert.False(r.Success);
        Assert.NotEmpty(r.Errors);
        return r;
    }

    [Fact]
    public void Parses_all_step_types_case_insensitively()
    {
        var w = Ok("""
            <?xml version="1.0" encoding="UTF-8"?>
            <Workout_File>
              <Author>Me</Author><NAME>Mixed</NAME><description>d</description>
              <tags><tag name="Threshold"/><tag name="Custom"/></tags>
              <workout>
                <warmup duration="600" powerlow=".25" powerhigh="0.75"/>
                <SteadyState Duration="300.4" Power="0.9" Cadence="95">
                  <TextEvent TimeOffset="10" Message="hello"/>
                  <textevent timeoffset="" mssage="typo tolerated"/>
                </SteadyState>
                <IntervalsT Repeat="3" OnDuration="60" OffDuration="30" OnPower="1.2" OffPower="0.5" Cadence="100" CadenceResting="85"/>
                <Ramp Duration="120" PowerLow="0.6" PowerHigh="0.9"/>
                <Freeride Duration="180" FlatRoad="1"/>
                <MaxEffort Duration="20"/>
                <SteadyState Duration="60" PowerLow="0.5" PowerHigh="0.7"/>
                <Cooldown Duration="300" PowerLow="0.5" PowerHigh="0.30000001"/>
              </workout>
            </Workout_File>
            """);
        Assert.Equal("Mixed", w.Name);
        Assert.Equal("Threshold", w.Category);
        Assert.Equal(8, w.Steps.Count);
        Assert.Equal(WorkoutStepKind.Warmup, w.Steps[0].Kind);
        Assert.Equal(0.25, w.Steps[0].PowerLow);
        Assert.Equal(300, w.Steps[1].DurationSeconds);
        Assert.Equal(95, w.Steps[1].Cadence);
        Assert.Equal(2, w.Steps[1].TextEvents.Count);
        Assert.Equal("typo tolerated", w.Steps[1].TextEvents[0].Message); // offset 0 sorts first
        var iv = w.Steps[2];
        Assert.Equal((3, 60, 30, 1.2, 0.5, 100, 85), (iv.Repeat, iv.OnDurationSeconds, iv.OffDurationSeconds, iv.OnPowerLow, iv.OffPowerHigh, iv.Cadence!.Value, iv.CadenceResting!.Value));
        Assert.Equal(270, iv.TotalSeconds);
        Assert.Equal(WorkoutStepKind.FreeRide, w.Steps[4].Kind);
        Assert.Equal(0.6, w.Steps[6].PowerLow, 9); // SteadyState with low/high → average
        Assert.Equal(0.30000001, w.Steps[7].PowerHigh, 9); // descending cool-down kept as written
        Assert.Equal(600 + 300 + 270 + 120 + 180 + 20 + 60 + 300, w.TotalSeconds);
    }

    [Fact]
    public void Defaults_and_tolerances()
    {
        var r = ZwoParser.Parse("""
            <workout_file><workout>
              <IntervalsT OnDuration="30" OffDuration="0" OnPower="1.5"/>
              <Warmup Duration="60" Power="0.5"/>
              <Warmup Duration="60" PowerLow="0.7" PowerHigh="0.5"/>
              <SolidState Duration="60" Power="0.8"/>
              <gameplayevent Duration="30"/>
              <TextNotification/>
              <textevent timeoffset="5" message="top-level"/>
            </workout><unknownHeader/></workout_file>
            """, "My File");
        Assert.True(r.Success, string.Join("; ", r.Issues));
        var w = r.Workout!;
        Assert.Equal("My File", w.Name);
        Assert.Equal(1, w.Steps[0].Repeat);
        Assert.Equal(30, w.Steps[0].TotalSeconds);
        Assert.Equal(0.5, w.Steps[1].PowerHigh);
        Assert.Equal(WorkoutStepKind.FreeRide, w.Steps[4].Kind); // unknown element with a duration keeps its time
        Assert.Single(w.WorkoutTextEvents);
        Assert.Contains(r.Warnings, i => i.Message.Contains("warm-up power goes down"));
        Assert.Contains(r.Warnings, i => i.Message.Contains("SolidState"));
        Assert.Contains(r.Warnings, i => i.Message.Contains("gameplayevent"));
        Assert.Contains(r.Warnings, i => i.Message.Contains("TextNotification"));
        Assert.Contains(r.Warnings, i => i.Message.Contains("unknownHeader"));
    }

    [Theory]
    [InlineData("<workout_file><sportType>run</sportType><workout><SteadyState Duration=\"60\" Power=\"1\"/></workout></workout_file>", "run")]
    [InlineData("<workout_file><durationType>distance</durationType><workout><SteadyState Duration=\"60\" Power=\"1\"/></workout></workout_file>", "distance")]
    [InlineData("<workout_file><ftpOverride>250</ftpOverride><workout><SteadyState Duration=\"60\" Power=\"1\"/></workout></workout_file>", "ftpOverride")]
    [InlineData("<workout_file><workout><SteadyState Duration=\"60\" Power=\"250\"/></workout></workout_file>", "watts")]
    [InlineData("<workout_file><workout><SteadyState Duration=\"abc\" Power=\"1\"/></workout></workout_file>", "not a number")]
    [InlineData("<workout_file><workout><SteadyState Duration=\"0\" Power=\"1\"/></workout></workout_file>", "more than zero")]
    [InlineData("<workout_file><workout><SteadyState Duration=\"60\"/></workout></workout_file>", "no power")]
    [InlineData("<workout_file><workout></workout></workout_file>", "no steps")]
    [InlineData("<workout_file><name>x</name></workout_file>", "no <workout>")]
    [InlineData("<other/>", "workout_file")]
    [InlineData("not xml", "Not valid XML")]
    [InlineData("", "empty")]
    public void Refuses_unsupported_or_broken_files(string xml, string expected)
    {
        var r = Bad(xml);
        Assert.Contains(r.Errors, e => e.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dtds_are_refused()
    {
        Bad("""
            <!DOCTYPE workout_file [ <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini"> ]>
            <workout_file><name>&xxe;</name><workout><SteadyState Duration="60" Power="1"/></workout></workout_file>
            """);
    }

    [Fact]
    public void Writer_round_trips_every_step_type()
    {
        var w = new Workout
        {
            Name = "Round & trip <test>",
            Description = "quotes \" and ampersands &",
            Tags = ["Custom"],
            FtpTest = FtpTestKind.EightMinute,
            WorkoutTextEvents = [new(0, "Welcome")],
            Steps =
            [
                WorkoutStep.Ramp(WorkoutStepKind.Warmup, 300, 0.4, 0.7),
                WorkoutStep.Steady(120, 0.88, cadence: 90) with { TextEvents = [new(5, "Go")] },
                WorkoutStep.Intervals(4, 40, 1.3, 20, 0.45, cadence: 105, cadenceResting: 80),
                new WorkoutStep { Kind = WorkoutStepKind.Intervals, Repeat = 2, OnDurationSeconds = 60, OffDurationSeconds = 60, OnPowerLow = 0.9, OnPowerHigh = 1.1, OffPowerLow = 0.5, OffPowerHigh = 0.5 },
                WorkoutStep.Ramp(WorkoutStepKind.Ramp, 60, 0.6, 1.0),
                WorkoutStep.FreeRide(90),
                WorkoutStep.MaxEffort(15),
                WorkoutStep.Ramp(WorkoutStepKind.Cooldown, 300, 0.6, 0.35),
            ],
        };
        var xml = ZwoWriter.Write(w);
        var back = ZwoParser.Parse(xml);
        Assert.True(back.Success, string.Join("; ", back.Issues));
        Assert.Empty(back.Warnings);
        var r = back.Workout!;
        Assert.Equal(w.Name, r.Name);
        Assert.Equal(w.Description, r.Description);
        Assert.Equal(FtpTestKind.EightMinute, r.FtpTest);
        Assert.Equal(w.Steps.Count, r.Steps.Count);
        for (var i = 0; i < w.Steps.Count; i++)
        {
            var a = w.Steps[i];
            var b = r.Steps[i];
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.TotalSeconds, b.TotalSeconds);
            Assert.Equal(a.PowerLow, b.PowerLow, 4);
            Assert.Equal(a.PowerHigh, b.PowerHigh, 4);
            Assert.Equal(a.OnPowerLow, b.OnPowerLow, 4);
            Assert.Equal(a.OnPowerHigh, b.OnPowerHigh, 4);
            Assert.Equal(a.OffPowerLow, b.OffPowerLow, 4);
            Assert.Equal(a.Cadence, b.Cadence);
            Assert.Equal(a.CadenceResting, b.CadenceResting);
            Assert.Equal(a.TextEvents, b.TextEvents);
        }
        Assert.Equal(w.WorkoutTextEvents, r.WorkoutTextEvents);
    }

    [Fact]
    public void Watts_workouts_export_as_ftp_fractions()
    {
        var w = new Workout { Name = "W", PowerUnit = WorkoutPowerUnit.Watts, Steps = [WorkoutStep.Steady(60, 250)] };
        Assert.Throws<ArgumentException>(() => ZwoWriter.Write(w));
        var r = ZwoParser.Parse(ZwoWriter.Write(w, ftpForWatts: 200)).Workout!;
        Assert.Equal(1.25, r.Steps[0].PowerLow, 6);
    }
}

public class WorkoutLibraryTests
{
    [Fact]
    public void Every_built_in_workout_parses_cleanly_and_has_the_expected_length()
    {
        var expected = new Dictionary<string, int>
        {
            ["recovery-spin-30.zwo"] = 1800, ["endurance-z2-60.zwo"] = 3600, ["tempo-3x12.zwo"] = 3780,
            ["sweet-spot-3x10.zwo"] = 3600, ["sweet-spot-2x20.zwo"] = 3900, ["threshold-2x20.zwo"] = 4260,
            ["threshold-3x12.zwo"] = 3780, ["over-unders-3x12.zwo"] = 3960, ["vo2max-5x5.zwo"] = 3900,
            ["vo2max-3x8.zwo"] = 3060, ["30-30-intervals.zwo"] = 2700, ["40-20-intervals.zwo"] = 3240,
            ["30-15-intervals.zwo"] = 3195, ["tabata-style-20-10.zwo"] = 2400, ["sprints-6x10s.zwo"] = 3000,
            ["pyramid-1-2-3-4-3-2-1.zwo"] = 2820, ["cadence-drills.zwo"] = 3180, ["openers.zwo"] = 2100,
            ["ftp-test-20-minute.zwo"] = 4860, ["ftp-test-ramp.zwo"] = 1740, ["ftp-test-8-minute.zwo"] = 3360,
        };
        var lib = WorkoutLibrary.BuiltIn;
        Assert.Equal(expected.Count, lib.Count);
        foreach (var e in lib)
        {
            var file = e.Key["builtin:".Length..];
            Assert.Equal(expected[file], e.Workout.TotalSeconds);
            Assert.Equal(expected[file], e.Timeline.TotalSeconds);
            var reparsed = ZwoParser.Parse(e.Zwo);
            Assert.Empty(reparsed.Issues);
            Assert.False(string.IsNullOrWhiteSpace(e.Workout.Description), file);
            Assert.NotNull(e.Metrics.IntensityFactor);
            Assert.InRange(e.Metrics.IntensityFactor!.Value, 0.4, 1.2);
            // Writer output re-parses to the same timeline.
            var again = new WorkoutTimeline(ZwoParser.Parse(ZwoWriter.Write(e.Workout)).Workout!);
            Assert.Equal(e.Timeline.Segments.Select(s => (s.StartSecond, s.DurationSeconds, s.PowerStart, s.PowerEnd, s.Role)),
                again.Segments.Select(s => (s.StartSecond, s.DurationSeconds, s.PowerStart, s.PowerEnd, s.Role)));
        }
        Assert.Equal(3, WorkoutLibrary.FtpTests.Count());
        Assert.All(WorkoutLibrary.FtpTests, t => Assert.Equal(WorkoutLibrary.TestCategory, t.Category));
        Assert.Equal("Recovery", lib[0].Category);
        Assert.Equal(WorkoutLibrary.TestCategory, lib[^1].Category);
    }

    [Fact]
    public void Planned_metrics_follow_coggan()
    {
        var hour = new WorkoutTimeline(new Workout { Name = "h", Steps = [WorkoutStep.Steady(3600, 1.0)] });
        var m = WorkoutMetrics.For(hour, ftp: 250);
        Assert.Equal(1.0, m.IntensityFactor!.Value, 9);
        Assert.Equal(100, m.TrainingStressScore!.Value, 6);
        Assert.Equal(3600, m.ZoneSeconds[3]);
        Assert.Equal(900, m.WorkKilojoules!.Value, 6);

        var free = new WorkoutTimeline(new Workout { Name = "f", Steps = [WorkoutStep.FreeRide(600)] });
        Assert.Null(WorkoutMetrics.For(free).IntensityFactor);
    }
}

public class TimelineTests
{
    private static readonly Workout Sample = new()
    {
        Name = "t",
        Steps =
        [
            WorkoutStep.Ramp(WorkoutStepKind.Warmup, 100, 0.5, 0.7) with { TextEvents = [new(0, "start"), new(500, "never")] },
            WorkoutStep.Intervals(2, 30, 1.2, 20, 0.5),
            WorkoutStep.Intervals(1, 60, 0.95, 60, 1.05),
            WorkoutStep.Steady(40, 0.9),
            WorkoutStep.MaxEffort(10),
            WorkoutStep.Ramp(WorkoutStepKind.Cooldown, 50, 0.6, 0.4),
        ],
        WorkoutTextEvents = [new(105, "abs"), new(9999, "late")],
    };

    [Fact]
    public void Ramp_tests_number_their_steps_and_easy_edges_are_warm_up_and_cool_down()
    {
        var ramp = KineticTrainer.Workouts.Library.WorkoutLibrary.FtpTests.Single(e => e.Workout.FtpTest == FtpTestKind.Ramp).Timeline;
        Assert.Equal("Warm-up", ramp.Segments[0].Label);
        Assert.Equal("Ramp step 1 of 19", ramp.Segments[1].Label);
        Assert.Equal(SegmentRole.Work, ramp.Segments[1].Role);
        Assert.Equal("Cool-down", ramp.Segments[^1].Label);

        var w = new WorkoutTimeline(new Workout { Name = "e", Steps = [WorkoutStep.Steady(60, 0.5), WorkoutStep.Steady(60, 0.7), WorkoutStep.Steady(60, 0.95), WorkoutStep.Steady(60, 0.4)] });
        Assert.Equal(["Warm-up", "Endurance", "Effort", "Cool-down"], w.Segments.Select(x => x.Label));
        Assert.Equal([SegmentRole.Warmup, SegmentRole.Work, SegmentRole.Work, SegmentRole.Cooldown], w.Segments.Select(x => x.Role));
    }

    [Fact]
    public void Flattens_intervals_and_assigns_roles_and_labels()
    {
        var t = new WorkoutTimeline(Sample);
        Assert.Equal(100 + 100 + 120 + 40 + 10 + 50, t.TotalSeconds);
        Assert.Equal(
            ["Warm-up", "Interval 1 of 2", "Recovery 1 of 2", "Interval 2 of 2", "Recovery 2 of 2", "Interval", "Interval (part 2)", "Effort", "Max effort", "Cool-down"],
            t.Segments.Select(s => s.Label));
        Assert.Equal(SegmentRole.Work, t.Segments[6].Role); // over-under "off" at 105 % is work, not recovery
        Assert.Equal([100, 130, 150, 180, 200, 260, 320, 360, 370], t.Segments.Skip(1).Select(s => s.StartSecond));
        Assert.Equal([0, 105], t.TextEvents.Select(e => e.AtSecond));
    }

    [Fact]
    public void Looks_up_segments_and_interpolates_ramps()
    {
        var t = new WorkoutTimeline(Sample);
        Assert.Equal(-1, t.SegmentIndexAt(-0.1));
        Assert.Equal(0, t.SegmentIndexAt(0));
        Assert.Equal(0, t.SegmentIndexAt(99.99));
        Assert.Equal(1, t.SegmentIndexAt(100));
        Assert.Equal(9, t.SegmentIndexAt(419.5));
        Assert.Equal(-1, t.SegmentIndexAt(420));
        Assert.Equal(0.5, t.TargetAt(0)!.Value, 9);
        Assert.Equal(0.6, t.TargetAt(50)!.Value, 9);
        Assert.Equal(300, t.TargetWattsAt(50, 250, 2.0)); // 0.6 * 250 * 2
        Assert.Null(t.TargetAt(365)); // max effort
        Assert.Null(t.TargetWattsAt(50, null));
        Assert.Equal("MAX", t.DescribeTarget(t.Segments[8], 250));
        Assert.Equal("125–175 W", t.DescribeTarget(t.Segments[0], 250));
        Assert.Equal("50–70% FTP", t.DescribeTarget(t.Segments[0], null));
        Assert.Equal("300 W", t.DescribeTarget(t.Segments[1], 250));
        Assert.Equal("300 W (120%)", t.DescribeTargetWithPercent(t.Segments[1], 250));
        Assert.Equal("125–175 W (50–70%)", t.DescribeTargetWithPercent(t.Segments[0], 250));
        Assert.Equal("MAX", t.DescribeTargetWithPercent(t.Segments[8], 250));
        var series = t.FractionSeries();
        Assert.Equal(420, series.Length);
        Assert.Null(series[365]);
        Assert.Equal(1.2, series[100]);
    }
}
