namespace KineticTrainer.Core.Statistics;

/// <summary>One ride's contribution to trends.</summary>
public readonly record struct LoadEntry(DateTimeOffset Start, double DurationSeconds, double DistanceMeters, double? Tss, double? WorkKilojoules);

/// <summary>Totals for one week (Monday-based, local time) or month.</summary>
public sealed record PeriodTotals(DateOnly PeriodStart, int Rides, double Seconds, double Meters, double Tss, int RidesWithTss, double WorkKilojoules);

/// <summary>Daily fitness/fatigue values (the Performance Manager model).</summary>
public readonly record struct LoadPoint(DateOnly Day, double DailyTss, double Fitness, double Fatigue)
{
    /// <summary>Form = yesterday's fitness minus yesterday's fatigue is the textbook definition; this uses today's values.</summary>
    public double Form => Fitness - Fatigue;
}

/// <summary>
/// Trend arithmetic. TSS only exists for rides recorded with an FTP (TSS = hours × IF² × 100 with IF = NP / FTP),
/// so rides without it add time and distance but no load. Fitness (CTL) and fatigue (ATL) are exponentially weighted
/// averages of daily TSS with the usual 42- and 7-day time constants.
/// </summary>
public static class TrainingLoad
{
    public const int FitnessDays = 42;
    public const int FatigueDays = 7;

    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static DateOnly MonthStart(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>Totals per week for <paramref name="weeks"/> weeks ending with the week containing <paramref name="today"/> (oldest first, empty weeks included).</summary>
    public static IReadOnlyList<PeriodTotals> Weekly(IEnumerable<LoadEntry> entries, DateOnly today, int weeks, TimeZoneInfo? zone = null)
    {
        var last = WeekStart(today);
        var starts = Enumerable.Range(0, weeks).Select(i => last.AddDays(-7 * (weeks - 1 - i))).ToList();
        return Aggregate(entries, starts, d => WeekStart(d), zone);
    }

    /// <summary>Totals per calendar month for <paramref name="months"/> months ending with the current month.</summary>
    public static IReadOnlyList<PeriodTotals> Monthly(IEnumerable<LoadEntry> entries, DateOnly today, int months, TimeZoneInfo? zone = null)
    {
        var last = MonthStart(today);
        var starts = Enumerable.Range(0, months).Select(i => last.AddMonths(-(months - 1 - i))).ToList();
        return Aggregate(entries, starts, MonthStart, zone);
    }

    /// <summary>Daily fitness/fatigue from <paramref name="from"/> to <paramref name="to"/> inclusive. Rides before
    /// <paramref name="from"/> seed the averages so the first days are not artificially low.</summary>
    public static IReadOnlyList<LoadPoint> FitnessFatigue(IEnumerable<LoadEntry> entries, DateOnly from, DateOnly to, TimeZoneInfo? zone = null)
    {
        var daily = new Dictionary<DateOnly, double>();
        DateOnly? first = null;
        foreach (var e in entries)
        {
            if (e.Tss is not { } tss || !double.IsFinite(tss) || tss <= 0)
            {
                continue;
            }
            var day = LocalDay(e.Start, zone);
            daily[day] = daily.GetValueOrDefault(day) + tss;
            first = first is null || day < first ? day : first;
        }
        var result = new List<LoadPoint>();
        if (to < from)
        {
            return result;
        }
        var start = first is { } f && f < from ? f : from;
        double ctl = 0, atl = 0;
        var kCtl = 1 - Math.Exp(-1.0 / FitnessDays);
        var kAtl = 1 - Math.Exp(-1.0 / FatigueDays);
        for (var d = start; d <= to; d = d.AddDays(1))
        {
            var tss = daily.GetValueOrDefault(d);
            ctl += (tss - ctl) * kCtl;
            atl += (tss - atl) * kAtl;
            if (d >= from)
            {
                result.Add(new LoadPoint(d, tss, ctl, atl));
            }
        }
        return result;
    }

    private static IReadOnlyList<PeriodTotals> Aggregate(IEnumerable<LoadEntry> entries, IReadOnlyList<DateOnly> starts, Func<DateOnly, DateOnly> bucket, TimeZoneInfo? zone)
    {
        var index = starts.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        var rides = new int[starts.Count];
        var secs = new double[starts.Count];
        var meters = new double[starts.Count];
        var tss = new double[starts.Count];
        var withTss = new int[starts.Count];
        var kj = new double[starts.Count];
        foreach (var e in entries)
        {
            if (!index.TryGetValue(bucket(LocalDay(e.Start, zone)), out var i))
            {
                continue;
            }
            rides[i]++;
            secs[i] += Math.Max(0, e.DurationSeconds);
            meters[i] += Math.Max(0, e.DistanceMeters);
            if (e.Tss is { } t && double.IsFinite(t))
            {
                tss[i] += t;
                withTss[i]++;
            }
            if (e.WorkKilojoules is { } w && double.IsFinite(w))
            {
                kj[i] += w;
            }
        }
        return starts.Select((s, i) => new PeriodTotals(s, rides[i], secs[i], meters[i], tss[i], withTss[i], kj[i])).ToArray();
    }

    private static DateOnly LocalDay(DateTimeOffset t, TimeZoneInfo? zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, zone ?? TimeZoneInfo.Local).DateTime);
}
