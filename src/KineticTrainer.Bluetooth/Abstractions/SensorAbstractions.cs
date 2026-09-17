using KineticTrainer.Core.Settings;

namespace KineticTrainer.Bluetooth.Abstractions;

/// <summary>Which trainer protocol a connected sensor speaks.</summary>
public enum TrainerProtocolKind
{
    /// <summary>Kinetic's custom inRide 2 service E9410100 (obfuscated 20-byte power packets).</summary>
    InRideCustom,

    /// <summary>Bluetooth SIG Fitness Machine Service 0x1826 (Indoor Bike Data).</summary>
    FitnessMachine,
}

/// <summary>A BLE peripheral seen while scanning.</summary>
public sealed record DiscoveredSensor(
    ulong Address,
    string? Name,
    short Rssi,
    bool AdvertisesInRideService,
    DateTimeOffset LastSeen,
    bool IsSimulated = false,
    SensorAddressType AddressType = SensorAddressType.Unspecified,
    bool AdvertisesFitnessMachineService = false)
{
    public string AddressText => FormatAddress(Address);

    /// <summary>True when either the name or the advertised services identify a trainer sensor we can use.</summary>
    public bool LooksLikeInRide =>
        AdvertisesInRideService || AdvertisesFitnessMachineService ||
        (Name is not null && (Name.Contains("inride", StringComparison.OrdinalIgnoreCase) ||
                              Name.Contains("kinetic", StringComparison.OrdinalIgnoreCase)));

    public static string FormatAddress(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));
}

/// <summary>Values read from the Device Information Service (and Battery Service) after connecting.</summary>
public sealed record SensorDeviceInfo(
    string? Name,
    byte[]? SystemId,
    string? FirmwareRevision,
    string? HardwareRevision,
    string? ManufacturerName,
    string? ModelNumber = null,
    byte? BatteryPercent = null)
{
    public string? SystemIdHex => SystemId is null ? null : Convert.ToHexStringLower(SystemId);

    public string Describe() =>
        $"name='{Name}' model='{ModelNumber ?? "?"}' mfr='{ManufacturerName ?? "?"}' fw={FirmwareRevision ?? "?"} hw={HardwareRevision ?? "?"} " +
        $"systemId={SystemIdHex ?? "(none)"} ({SystemId?.Length ?? 0} bytes) battery={(BatteryPercent is { } b ? b + "%" : "?")}";
}

public interface ISensorScanner : IAsyncDisposable
{
    event Action<DiscoveredSensor>? SensorSeen;

    bool IsScanning { get; }

    Task StartAsync(CancellationToken ct);

    Task StopAsync();
}

/// <summary>
/// An established GATT connection to one sensor, independent of which protocol it speaks.
/// The three notification streams map onto each protocol as follows:
///   inRide custom : DataNotified = Power (E9410101), StatusNotified = Configuration (E9410104), ControlResponded = unused
///   FTMS          : DataNotified = Indoor Bike Data (0x2AD2), StatusNotified = Fitness Machine Status (0x2ADA),
///                   ControlResponded = Control Point indications (0x2AD9)
/// </summary>
public interface ISensorLink : IAsyncDisposable
{
    ulong Address { get; }

    SensorDeviceInfo DeviceInfo { get; }

    bool IsConnected { get; }

    TrainerProtocolKind Protocol { get; }

    /// <summary>The address type Windows actually used for this connection (recorded so reconnects need no guessing).</summary>
    SensorAddressType AddressType { get; }

    /// <summary>True when the status/configuration characteristic could be subscribed to.</summary>
    bool StatusSupportsNotify { get; }

    /// <summary>Primary telemetry notifications: inRide power packets, or FTMS Indoor Bike Data.</summary>
    event Action<byte[], DateTimeOffset>? DataNotified;

    /// <summary>Secondary notifications: inRide configuration, or FTMS Fitness Machine Status.</summary>
    event Action<byte[], DateTimeOffset>? StatusNotified;

    /// <summary>Control point responses (FTMS indications). Never raised for the inRide custom protocol.</summary>
    event Action<byte[], DateTimeOffset>? ControlResponded;

    /// <summary>Raised once when the link drops for any reason other than <see cref="IAsyncDisposable.DisposeAsync"/>.</summary>
    event Action<string>? Disconnected;

    /// <summary>Reads the inRide Configuration characteristic, or the FTMS Fitness Machine Feature.</summary>
    Task<byte[]> ReadConfigurationAsync(CancellationToken ct);

    Task WriteControlPointAsync(byte[] payload, CancellationToken ct);
}

public sealed class SensorConnectException : Exception
{
    public SensorConnectException(string message, bool deviceUnreachable = false, Exception? inner = null) : base(message, inner)
    {
        DeviceUnreachable = deviceUnreachable;
    }

    /// <summary>True when the device was simply not in range / powered off (expected during reconnection).</summary>
    public bool DeviceUnreachable { get; }
}

public interface ISensorConnector
{
    /// <summary>Connects, discovers services/characteristics, reads device info and subscribes to notifications.</summary>
    /// <param name="addressType">Address type from the advertisement; <see cref="SensorAddressType.Unspecified"/> lets the platform guess.</param>
    /// <exception cref="SensorConnectException">When any step fails; the link is fully cleaned up.</exception>
    Task<ISensorLink> ConnectAsync(ulong address, SensorAddressType addressType, CancellationToken ct);
}

/// <summary>A source of scanners and connectors (real Bluetooth, or the simulator).</summary>
public interface ISensorPlatform
{
    string Name { get; }

    ISensorScanner CreateScanner();

    ISensorConnector Connector { get; }

    /// <summary>True if this platform owns the given address (the simulator uses a reserved address range).</summary>
    bool Owns(ulong address);
}
