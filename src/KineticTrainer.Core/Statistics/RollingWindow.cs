namespace KineticTrainer.Core.Statistics;

/// <summary>
/// Keeps (time, value) samples for a sliding time window. Used for the live 3 s / 10 s power averages and the
/// rolling power graph. Thread-safe; cheap enough to update at sensor rate.
/// </summary>
public sealed class RollingWindow
{
    private readonly object _gate = new();
    private readonly Queue<(DateTimeOffset T, double V)> _items = new();
    private TimeSpan _span;

    public RollingWindow(TimeSpan span)
    {
        _span = span;
    }

    public TimeSpan Span
    {
        get { lock (_gate) return _span; }
        set { lock (_gate) { _span = value; } }
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public void Add(DateTimeOffset t, double value)
    {
        lock (_gate)
        {
            _items.Enqueue((t, value));
            Trim(t);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    /// <summary>Mean of the samples no older than <paramref name="window"/> before <paramref name="now"/>.</summary>
    public double? Average(TimeSpan window, DateTimeOffset now)
    {
        lock (_gate)
        {
            double sum = 0;
            var n = 0;
            foreach (var (t, v) in _items)
            {
                if (now - t <= window && t <= now)
                {
                    sum += v;
                    n++;
                }
            }
            return n == 0 ? null : sum / n;
        }
    }

    /// <summary>Samples in the window as (seconds relative to <paramref name="now"/> — negative, value) points.</summary>
    public IReadOnlyList<SeriesDownsampler.Point> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            Trim(now);
            return _items.Select(i => new SeriesDownsampler.Point((i.T - now).TotalSeconds, i.V)).ToList();
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_items.Count > 0 && now - _items.Peek().T > _span)
        {
            _items.Dequeue();
        }
    }
}
