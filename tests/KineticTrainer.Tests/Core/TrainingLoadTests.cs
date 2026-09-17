using KineticTrainer.Core.Statistics;
using Xunit;

namespace KineticTrainer.Tests.Core;

public class TrainingLoadTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static LoadEntry E(int year, int month, int day, double hours, double km, double? tss) =>
        new(new DateTimeOffset(year, month, day, 18, 0, 0, TimeSpan.Zero), hours * 3600, km * 1000, tss, hours * 500);

    [Fact]
    public void Weeks_start_on_monday_and_include_empty_weeks()
    {
        Assert.Equal(new DateOnly(2026, 9, 14), TrainingLoad.WeekStart(new DateOnly(2026, 9, 16))); // Wednesday
        Assert.Equal(new DateOnly(2026, 9, 14), TrainingLoad.WeekStart(new DateOnly(2026, 9, 14))); // Monday
        Assert.Equal(new DateOnly(2026, 9, 14), TrainingLoad.WeekStart(new DateOnly(2026, 9, 20))); // Sunday

        var entries = new[]
        {
            E(2026, 9, 15, 1, 30, 60),
            E(2026, 9, 16, 0.5, 15, null), // no FTP: time and distance, no load
            E(2026, 9, 1, 2, 60, 110),
            E(2025, 1, 1, 1, 1, 50), // outside the range
        };
        var weeks = TrainingLoad.Weekly(entries, new DateOnly(2026, 9, 16), 4, Utc);
        Assert.Equal([new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 14)], weeks.Select(w => w.PeriodStart));
        Assert.Equal([0, 1, 0, 2], weeks.Select(w => w.Rides));
        Assert.Equal(1.5 * 3600, weeks[3].Seconds);
        Assert.Equal(45000, weeks[3].Meters);
        Assert.Equal(60, weeks[3].Tss);
        Assert.Equal(1, weeks[3].RidesWithTss);
        Assert.Equal(750, weeks[3].WorkKilojoules);

        var months = TrainingLoad.Monthly(entries, new DateOnly(2026, 9, 16), 3, Utc);
        Assert.Equal([new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1)], months.Select(m => m.PeriodStart));
        Assert.Equal(3, months[2].Rides);
        Assert.Equal(170, months[2].Tss);
    }

    [Fact]
    public void Fitness_and_fatigue_follow_the_exponential_model()
    {
        // 100 TSS every day for a long time converges to 100 for both.
        var start = new DateOnly(2026, 1, 1);
        var entries = Enumerable.Range(0, 400).Select(i =>
        {
            var d = start.AddDays(i);
            return new LoadEntry(new DateTimeOffset(d.Year, d.Month, d.Day, 12, 0, 0, TimeSpan.Zero), 3600, 0, 100, null);
        }).ToList();
        var points = TrainingLoad.FitnessFatigue(entries, start.AddDays(399), start.AddDays(399), Utc);
        Assert.Single(points);
        Assert.Equal(100, points[0].Fitness, 1);
        Assert.Equal(100, points[0].Fatigue, 6);
        Assert.Equal(0, points[0].Form, 1);

        // A single 100 TSS ride: fatigue reacts faster than fitness, and both decay.
        var one = new[] { E(2026, 3, 1, 1, 30, 100) };
        var series = TrainingLoad.FitnessFatigue(one, new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 10), Utc);
        Assert.Equal(11, series.Count);
        Assert.Equal(0, series[0].Fatigue);
        Assert.Equal(100 * (1 - Math.Exp(-1.0 / 7)), series[1].Fatigue, 9);
        Assert.Equal(100 * (1 - Math.Exp(-1.0 / 42)), series[1].Fitness, 9);
        Assert.True(series[1].Form < 0);
        Assert.True(series[10].Fatigue < series[1].Fatigue);
        Assert.Empty(TrainingLoad.FitnessFatigue(one, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 1), Utc));
    }
}
