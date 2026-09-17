namespace KineticTrainer.Core.Timing;

/// <summary>
/// Retry delays that grow exponentially up to a cap, with optional jitter, so reconnection
/// never hammers the Bluetooth stack.
/// </summary>
public sealed class ExponentialBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly double _factor;
    private readonly double _jitter;
    private readonly Random _random;

    public ExponentialBackoff(TimeSpan initial, TimeSpan max, double factor = 2.0, double jitter = 0.2, Random? random = null)
    {
        if (initial <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initial));
        if (max < initial) throw new ArgumentOutOfRangeException(nameof(max));
        if (factor < 1.0) throw new ArgumentOutOfRangeException(nameof(factor));
        if (jitter is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(jitter));
        _initial = initial;
        _max = max;
        _factor = factor;
        _jitter = jitter;
        _random = random ?? Random.Shared;
    }

    /// <summary>Number of delays handed out since the last reset.</summary>
    public int Attempt { get; private set; }

    public TimeSpan Next()
    {
        var baseMs = Math.Min(_max.TotalMilliseconds, _initial.TotalMilliseconds * Math.Pow(_factor, Attempt));
        Attempt++;
        var spread = baseMs * _jitter;
        var ms = baseMs - spread + (_random.NextDouble() * 2 * spread);
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 1, _max.TotalMilliseconds));
    }

    public void Reset() => Attempt = 0;
}
