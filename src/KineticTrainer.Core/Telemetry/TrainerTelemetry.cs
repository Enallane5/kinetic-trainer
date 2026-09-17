namespace KineticTrainer.Core.Telemetry;

/// <summary>Connection lifecycle of the trainer sensor, as shown to the rider.</summary>
public enum TrainerConnectionState
{
    Disconnected,
    Searching,
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>Trainer-agnostic operating mode reported by the sensor.</summary>
public enum TrainerSensorMode
{
    Unknown,
    Normal,
    CalibrationIdle,
    CalibrationReady,
    CalibrationActive,
}

/// <summary>
/// The single authoritative, already-interpreted telemetry sample shared by every consumer
/// (live UI, simulator bridge, and later the recorder, statistics and workout engine).
/// All values are SI units. Sensor values are <c>null</c> whenever there is no current
/// sensor data (disconnected, reconnecting, or the data stream went stale) - consumers must
/// never substitute fabricated values.
/// </summary>
public sealed record TrainerTelemetry
{
    /// <summary>Monotonic sequence number assigned by the publisher.</summary>
    public long Sequence { get; init; }

    /// <summary>When the underlying sensor packet was received (or the state change happened).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    public required TrainerConnectionState ConnectionState { get; init; }

    /// <summary>True when the sensor values below come from a current, accepted packet.</summary>
    public bool IsDataValid { get; init; }

    public int? PowerWatts { get; init; }

    public double? CadenceRpm { get; init; }

    public double? RollerRpm { get; init; }

    /// <summary>Ground-equivalent speed = roller circumference x roller RPM / 60.</summary>
    public double? SpeedMetersPerSecond { get; init; }

    /// <summary>Sensor's coasting detection (acceleration based), when available.</summary>
    public bool? Coasting { get; init; }

    public TrainerSensorMode SensorMode { get; init; } = TrainerSensorMode.Unknown;

    /// <summary>Hex system identifier of the sensor, when known.</summary>
    public string? SensorId { get; init; }

    public string? SensorName { get; init; }

    /// <summary>True when the packets come from the built-in simulated sensor, not hardware.</summary>
    public bool IsSimulated { get; init; }

    /// <summary>Creates a sample that carries only connection state (no sensor values).</summary>
    public static TrainerTelemetry NoData(DateTimeOffset timestamp, TrainerConnectionState state, string? sensorId = null,
        string? sensorName = null, bool simulated = false) => new()
    {
        Timestamp = timestamp,
        ConnectionState = state,
        IsDataValid = false,
        SensorId = sensorId,
        SensorName = sensorName,
        IsSimulated = simulated,
    };
}
