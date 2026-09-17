using KineticTrainer.Core.Streaming;

namespace KineticTrainer.Core.Telemetry;

/// <summary>Read side of the telemetry stream.</summary>
public interface ITelemetryStream
{
    /// <summary>Most recently published sample, or null before the first one.</summary>
    TrainerTelemetry? Latest { get; }

    Broadcast<TrainerTelemetry>.Subscription Subscribe(string name, int capacity = 64);
}

/// <summary>Write side of the telemetry stream. Only the sensor pipeline publishes.</summary>
public interface ITelemetryPublisher
{
    void Publish(TrainerTelemetry telemetry);
}

/// <summary>The one telemetry stream every consumer reads from.</summary>
public sealed class TelemetryHub : ITelemetryStream, ITelemetryPublisher
{
    private readonly Broadcast<TrainerTelemetry> _broadcast = new();
    private TrainerTelemetry? _latest;
    private long _sequence;

    public TrainerTelemetry? Latest => Volatile.Read(ref _latest);

    public void Publish(TrainerTelemetry telemetry)
    {
        var stamped = telemetry with { Sequence = Interlocked.Increment(ref _sequence) };
        Volatile.Write(ref _latest, stamped);
        _broadcast.Publish(stamped);
    }

    public Broadcast<TrainerTelemetry>.Subscription Subscribe(string name, int capacity = 64) =>
        _broadcast.Subscribe(name, capacity);
}
