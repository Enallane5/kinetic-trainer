using KineticTrainer.Core.Streaming;

namespace KineticTrainer.Core.Diagnostics;

public enum DiagnosticCategory
{
    App,
    Scan,
    Connection,
    Gatt,
    Reconnect,
    PowerPacket,
    ConfigPacket,
    PacketRejected,
    PacketWarning,
    Command,
    Bridge,
    Settings,
}

public enum DiagnosticSeverity
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    DiagnosticCategory Category,
    DiagnosticSeverity Severity,
    string Message,
    string? RawHex = null)
{
    public override string ToString() =>
        RawHex is null
            ? $"{Timestamp:HH:mm:ss.fff} [{Severity}] {Category}: {Message}"
            : $"{Timestamp:HH:mm:ss.fff} [{Severity}] {Category}: {Message} | {RawHex}";
}

public interface IDiagnosticLog
{
    void Write(DiagnosticEvent evt);

    Broadcast<DiagnosticEvent>.Subscription Subscribe(string name, int capacity = 1024);

    /// <summary>The most recent events (oldest first), for views that open after the fact.</summary>
    IReadOnlyList<DiagnosticEvent> Recent();
}

/// <summary>
/// In-memory diagnostic log: non-blocking fan-out plus a small ring buffer of recent events.
/// Writing never throws and never blocks the caller (BLE callbacks, parsing loop).
/// </summary>
public sealed class DiagnosticLog : IDiagnosticLog
{
    private readonly Broadcast<DiagnosticEvent> _broadcast = new();
    private readonly DiagnosticEvent[] _ring;
    private readonly object _ringGate = new();
    private int _next;
    private int _count;
    private readonly TimeProvider _time;

    public DiagnosticLog(int recentCapacity = 2000, TimeProvider? time = null)
    {
        _ring = new DiagnosticEvent[Math.Max(16, recentCapacity)];
        _time = time ?? TimeProvider.System;
    }

    public void Write(DiagnosticEvent evt)
    {
        lock (_ringGate)
        {
            _ring[_next] = evt;
            _next = (_next + 1) % _ring.Length;
            _count = Math.Min(_count + 1, _ring.Length);
        }
        _broadcast.Publish(evt);
    }

    public void Write(DiagnosticCategory category, DiagnosticSeverity severity, string message, string? rawHex = null) =>
        Write(new DiagnosticEvent(_time.GetUtcNow(), category, severity, message, rawHex));

    public Broadcast<DiagnosticEvent>.Subscription Subscribe(string name, int capacity = 1024) =>
        _broadcast.Subscribe(name, capacity);

    public IReadOnlyList<DiagnosticEvent> Recent()
    {
        lock (_ringGate)
        {
            var result = new DiagnosticEvent[_count];
            var start = (_next - _count + _ring.Length) % _ring.Length;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _ring[(start + i) % _ring.Length];
            }
            return result;
        }
    }
}

public static class DiagnosticLogExtensions
{
    public static void Info(this IDiagnosticLog log, DiagnosticCategory category, string message, string? rawHex = null) =>
        log.Write(new DiagnosticEvent(DateTimeOffset.UtcNow, category, DiagnosticSeverity.Info, message, rawHex));

    public static void Warn(this IDiagnosticLog log, DiagnosticCategory category, string message, string? rawHex = null) =>
        log.Write(new DiagnosticEvent(DateTimeOffset.UtcNow, category, DiagnosticSeverity.Warning, message, rawHex));

    public static void Error(this IDiagnosticLog log, DiagnosticCategory category, string message, string? rawHex = null) =>
        log.Write(new DiagnosticEvent(DateTimeOffset.UtcNow, category, DiagnosticSeverity.Error, message, rawHex));

    public static void Debug(this IDiagnosticLog log, DiagnosticCategory category, string message, string? rawHex = null) =>
        log.Write(new DiagnosticEvent(DateTimeOffset.UtcNow, category, DiagnosticSeverity.Debug, message, rawHex));
}
