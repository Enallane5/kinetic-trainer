namespace KineticTrainer.Core.Timing;

/// <summary>Estimates event frequency (e.g. packets per second) over a sliding time window.</summary>
public sealed class RateEstimator
{
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _stamps = new();
    private readonly object _gate = new();

    public RateEstimator(TimeSpan window)
    {
        _window = window;
    }

    public void Mark(DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            _stamps.Enqueue(timestamp);
            Trim(timestamp);
        }
    }

    /// <summary>Events per second in the window, or null if fewer than two events.</summary>
    public double? RateHz(DateTimeOffset now)
    {
        lock (_gate)
        {
            Trim(now);
            if (_stamps.Count < 2)
            {
                return null;
            }
            var span = (_stamps.Last() - _stamps.Peek()).TotalSeconds;
            return span <= 0 ? null : (_stamps.Count - 1) / span;
        }
    }

    /// <summary>Median interval between consecutive events in the window.</summary>
    public TimeSpan? MedianInterval(DateTimeOffset now)
    {
        lock (_gate)
        {
            Trim(now);
            if (_stamps.Count < 2)
            {
                return null;
            }
            var arr = _stamps.ToArray();
            var gaps = new double[arr.Length - 1];
            for (var i = 1; i < arr.Length; i++)
            {
                gaps[i - 1] = (arr[i] - arr[i - 1]).TotalMilliseconds;
            }
            Array.Sort(gaps);
            return TimeSpan.FromMilliseconds(gaps[gaps.Length / 2]);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _stamps.Clear();
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_stamps.Count > 0 && now - _stamps.Peek() > _window)
        {
            _stamps.Dequeue();
        }
    }
}
