using System.Text;
using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Settings;
using KineticTrainer.Protocol.Ftms;
using KineticTrainer.Protocol.InRide;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace KineticTrainer.Bluetooth.WinRt;

/// <summary>
/// Connect sequence:
///  1. BluetoothLEDevice.FromBluetoothAddressAsync with the address type seen in the advertisement
///  2. GattSession with MaintainConnection so Windows keeps the link up between operations
///  3. Full service discovery (uncached, retried) and a log line for EVERY service and characteristic found
///  4. Protocol selection: Kinetic custom E9410100 if present, otherwise Fitness Machine 0x1826
///  5. Device Information 180A (+ Battery 180F); subscribe to the protocol's data/status/control characteristics
/// Validated 2026-09-15 against a Kinetic "inRide" (HW 10, FW 1.2) that exposes FTMS only.
/// </summary>
public sealed class WinRtSensorConnector : ISensorConnector
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    private const int DiscoveryAttempts = 4;

    private static readonly Dictionary<string, string> KnownServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00001800"] = "Generic Access",
        ["00001801"] = "Generic Attribute",
        ["0000180a"] = "Device Information",
        ["0000180f"] = "Battery",
        ["00001816"] = "Cycling Speed and Cadence",
        ["00001818"] = "Cycling Power",
        ["00001826"] = "Fitness Machine (FTMS)",
        ["e9410100"] = "Kinetic inRide power (custom)",
        ["e9410200"] = "Kinetic Smart Control (custom)",
        ["00001530"] = "Nordic DFU",
    };

    private readonly IDiagnosticLog _log;

    public WinRtSensorConnector(IDiagnosticLog log)
    {
        _log = log;
    }

    public async Task<ISensorLink> ConnectAsync(ulong address, SensorAddressType addressType, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(OperationTimeout * 3);
        var token = timeoutCts.Token;

        BluetoothLEDevice? device = null;
        GattSession? session = null;
        var services = new List<GattDeviceService>();
        try
        {
            device = await GetDeviceAsync(address, addressType, token).ConfigureAwait(false);
            _log.Info(DiagnosticCategory.Gatt,
                $"Device object obtained: name='{device.Name}' id={device.DeviceId} status={device.ConnectionStatus} addrType={device.BluetoothAddressType} " +
                $"paired={device.DeviceInformation.Pairing.IsPaired} access={device.DeviceAccessInformation.CurrentStatus}");

            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token).ConfigureAwait(false);
            session.MaintainConnection = true;

            var access = await device.RequestAccessAsync().AsTask(token).ConfigureAwait(false);
            if (access != DeviceAccessStatus.Allowed)
            {
                _log.Warn(DiagnosticCategory.Gatt, $"RequestAccessAsync returned {access}; continuing");
            }

            // Full discovery, retried: Windows sometimes answers "Success, 0 services" while the link is still coming up.
            GattDeviceServicesResult? all = null;
            for (var attempt = 1; attempt <= DiscoveryAttempts; attempt++)
            {
                all = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
                _log.Info(DiagnosticCategory.Gatt,
                    $"Service discovery attempt {attempt}/{DiscoveryAttempts}: status={all.Status}{ProtocolError(all.ProtocolError)} services={all.Services.Count} " +
                    $"link={device.ConnectionStatus} session={session.SessionStatus}");
                if (all.Status == GattCommunicationStatus.Success && all.Services.Count > 0)
                {
                    break;
                }
                if (all.Status is GattCommunicationStatus.AccessDenied or GattCommunicationStatus.ProtocolError)
                {
                    break;
                }
                foreach (var s in all.Services)
                {
                    s.Dispose();
                }
                await Task.Delay(TimeSpan.FromMilliseconds(1500), token).ConfigureAwait(false);
            }
            if (all is null || all.Status != GattCommunicationStatus.Success)
            {
                throw new SensorConnectException(
                    $"Service discovery failed: {all?.Status}{ProtocolError(all?.ProtocolError)}",
                    deviceUnreachable: all?.Status is GattCommunicationStatus.Unreachable or null);
            }
            services.AddRange(all.Services);
            if (services.Count == 0)
            {
                throw new SensorConnectException(
                    "Windows connected but the sensor reported no GATT services at all (after " + DiscoveryAttempts + " attempts). " +
                    "Try again with the sensor awake (spin the roller); if it persists, remove the sensor from Windows Bluetooth settings if it is listed there.",
                    deviceUnreachable: true);
            }

            // Log everything the sensor exposes, and collect characteristics per service.
            var summary = new StringBuilder();
            var charsByService = new Dictionary<Guid, IReadOnlyList<GattCharacteristic>>();
            foreach (var svc in services)
            {
                _log.Info(DiagnosticCategory.Gatt, $"SERVICE {svc.Uuid} ({Describe(svc.Uuid)}) handle=0x{svc.AttributeHandle:X4}");
                summary.Append(summary.Length == 0 ? "" : ", ").Append(Describe(svc.Uuid));
                try
                {
                    var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
                    if (chars.Status != GattCommunicationStatus.Success)
                    {
                        _log.Warn(DiagnosticCategory.Gatt, $"  characteristics of {svc.Uuid}: {chars.Status}{ProtocolError(chars.ProtocolError)}");
                        continue;
                    }
                    charsByService[svc.Uuid] = chars.Characteristics;
                    foreach (var c in chars.Characteristics)
                    {
                        _log.Info(DiagnosticCategory.Gatt, $"  CHAR {c.Uuid} handle=0x{c.AttributeHandle:X4} props={c.CharacteristicProperties}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn(DiagnosticCategory.Gatt, $"  characteristics of {svc.Uuid}: {ex.Message}");
                }
            }

            // Device Information + Battery (both protocols).
            var info = await ReadDeviceInfoAsync(device, services, charsByService, token).ConfigureAwait(false);

            var kinetic = services.FirstOrDefault(s => s.Uuid == InRideUuids.PowerService);
            var ftms = services.FirstOrDefault(s => s.Uuid == FtmsUuids.FitnessMachineService);
            var primary = kinetic ?? ftms;
            if (primary is null)
            {
                var hasStandardPower = services.Any(s => Is16Bit(s.Uuid, 0x1818));
                var hasCsc = services.Any(s => Is16Bit(s.Uuid, 0x1816));
                var hint = hasStandardPower || hasCsc
                    ? " It speaks the Cycling Power/CSC profile, which this app does not decode yet."
                    : " Neither Kinetic's custom service (E9410100) nor the Fitness Machine service (0x1826) is present.";
                throw new SensorConnectException($"Sensor exposes: {summary}.{hint}");
            }

            // The protocol service's characteristics are essential: retry them rather than trusting the first sweep.
            if (!charsByService.TryGetValue(primary.Uuid, out var primaryChars) || primaryChars.Count == 0)
            {
                primaryChars = await GetCharacteristicsWithRetryAsync(primary, Describe(primary.Uuid), token).ConfigureAwait(false);
            }

            WinRtSensorLink link;
            if (kinetic is not null)
            {
                var kChars = primaryChars;
                var power = Find(kChars, InRideUuids.PowerCharacteristic, "Power");
                var config = Find(kChars, InRideUuids.ConfigurationCharacteristic, "Configuration");
                var control = Find(kChars, InRideUuids.ControlPointCharacteristic, "Control Point");
                link = new WinRtSensorLink(device, session, services, TrainerProtocolKind.InRideCustom, power, config, control, config, info, _log);
                await SubscribeAsync(link, power, "Power", required: true, token).ConfigureAwait(false);
                link.StatusSupportsNotify = await SubscribeAsync(link, config, "Configuration", required: false, token).ConfigureAwait(false);
            }
            else
            {
                var fChars = primaryChars;
                var bikeData = Find(fChars, FtmsUuids.IndoorBikeData, "Indoor Bike Data");
                var status = fChars.FirstOrDefault(c => c.Uuid == FtmsUuids.FitnessMachineStatus);
                var control = fChars.FirstOrDefault(c => c.Uuid == FtmsUuids.FitnessMachineControlPoint);
                var feature = fChars.FirstOrDefault(c => c.Uuid == FtmsUuids.FitnessMachineFeature);
                if (feature is null)
                {
                    _log.Warn(DiagnosticCategory.Gatt, "FTMS: no Fitness Machine Feature characteristic (0x2ACC); capabilities unknown");
                }
                link = new WinRtSensorLink(device, session, services, TrainerProtocolKind.FitnessMachine, bikeData, status, control, feature, info, _log);
                await SubscribeAsync(link, bikeData, "Indoor Bike Data", required: true, token).ConfigureAwait(false);
                if (status is not null)
                {
                    link.StatusSupportsNotify = await SubscribeAsync(link, status, "Fitness Machine Status", required: false, token).ConfigureAwait(false);
                }
                if (control is not null)
                {
                    // Responses to control point writes arrive as indications; subscribe before ever writing.
                    await SubscribeAsync(link, control, "Fitness Machine Control Point", required: false, token).ConfigureAwait(false);
                }
            }

            link.Attach();
            return link;
        }
        catch (Exception ex) when (ex is not SensorConnectException)
        {
            Cleanup(services, session, device);
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new SensorConnectException("Timed out talking to the sensor.", deviceUnreachable: true, inner: ex);
            }
            if (ex is OperationCanceledException)
            {
                throw;
            }
            throw new SensorConnectException($"Bluetooth error: {ex.Message}", inner: ex);
        }
        catch
        {
            Cleanup(services, session, device);
            throw;
        }
    }

    private async Task<IReadOnlyList<GattCharacteristic>> GetCharacteristicsWithRetryAsync(GattDeviceService service, string label, CancellationToken token)
    {
        GattCharacteristicsResult? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // AccessDenied here usually means a previous (crashed/killed) client still holds the service; asking for
            // access explicitly and retrying clears it within a few seconds.
            var access = await service.RequestAccessAsync().AsTask(token).ConfigureAwait(false);
            if (access != DeviceAccessStatus.Allowed)
            {
                _log.Warn(DiagnosticCategory.Gatt, $"RequestAccessAsync on {label} returned {access}");
            }
            last = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
            if (last.Status == GattCommunicationStatus.Success && last.Characteristics.Count > 0)
            {
                foreach (var c in last.Characteristics)
                {
                    _log.Info(DiagnosticCategory.Gatt, $"  CHAR {c.Uuid} handle=0x{c.AttributeHandle:X4} props={c.CharacteristicProperties} (retry {attempt})");
                }
                return last.Characteristics;
            }
            _log.Warn(DiagnosticCategory.Gatt, $"Characteristics of {label} attempt {attempt}/3: {last.Status}{ProtocolError(last.ProtocolError)} count={last.Characteristics.Count}");
            await Task.Delay(TimeSpan.FromMilliseconds(1000), token).ConfigureAwait(false);
        }
        throw new SensorConnectException($"The {label} service is present but its characteristics could not be read ({last?.Status}{ProtocolError(last?.ProtocolError)}).",
            deviceUnreachable: last?.Status == GattCommunicationStatus.Unreachable);
    }

    private async Task<bool> SubscribeAsync(WinRtSensorLink link, GattCharacteristic c, string label, bool required, CancellationToken token)
    {
        var mode = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)
                ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : GattClientCharacteristicConfigurationDescriptorValue.None;
        if (mode == GattClientCharacteristicConfigurationDescriptorValue.None)
        {
            var msg = $"{label} characteristic has neither Notify nor Indicate (props={c.CharacteristicProperties})";
            if (required)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                throw new SensorConnectException(msg);
            }
            _log.Info(DiagnosticCategory.Gatt, msg + "; will read on demand");
            return false;
        }
        var cccd = await c.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(mode).AsTask(token).ConfigureAwait(false);
        if (cccd.Status != GattCommunicationStatus.Success)
        {
            var msg = $"Subscribing to {label} ({mode}) failed: {cccd.Status}{ProtocolError(cccd.ProtocolError)}";
            if (required)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                throw new SensorConnectException(msg, deviceUnreachable: cccd.Status == GattCommunicationStatus.Unreachable);
            }
            _log.Warn(DiagnosticCategory.Gatt, msg);
            return false;
        }
        _log.Info(DiagnosticCategory.Gatt, $"Subscribed to {label} ({mode})");
        return true;
    }

    private async Task<SensorDeviceInfo> ReadDeviceInfoAsync(BluetoothLEDevice device, List<GattDeviceService> services,
        Dictionary<Guid, IReadOnlyList<GattCharacteristic>> charsByService, CancellationToken token)
    {
        byte[]? systemId = null;
        string? firmware = null, hardware = null, manufacturer = null, model = null;
        byte? battery = null;

        var dis = services.FirstOrDefault(s => s.Uuid == InRideUuids.DeviceInformationService);
        if (dis is not null && charsByService.TryGetValue(dis.Uuid, out var disChars))
        {
            foreach (var c in disChars)
            {
                if (c.Uuid == InRideUuids.SystemIdCharacteristic)
                {
                    systemId = await ReadBytesAsync(c, "System ID", token).ConfigureAwait(false);
                }
                else if (c.Uuid == InRideUuids.FirmwareRevisionCharacteristic)
                {
                    firmware = await ReadStringAsync(c, "Firmware Revision", token).ConfigureAwait(false);
                }
                else if (c.Uuid == InRideUuids.HardwareRevisionCharacteristic)
                {
                    hardware = await ReadStringAsync(c, "Hardware Revision", token).ConfigureAwait(false);
                }
                else if (c.Uuid == InRideUuids.ManufacturerNameCharacteristic)
                {
                    manufacturer = await ReadStringAsync(c, "Manufacturer Name", token).ConfigureAwait(false);
                }
                else if (c.Uuid == FtmsUuids.ModelNumber)
                {
                    model = await ReadStringAsync(c, "Model Number", token).ConfigureAwait(false);
                }
            }
        }
        else
        {
            _log.Warn(DiagnosticCategory.Gatt, "Device Information service (180A) not present.");
        }
        if (systemId is not null)
        {
            _log.Info(DiagnosticCategory.Gatt, $"System ID = {Convert.ToHexStringLower(systemId)} ({systemId.Length} bytes)");
        }

        var batt = services.FirstOrDefault(s => s.Uuid == FtmsUuids.BatteryService);
        if (batt is not null && charsByService.TryGetValue(batt.Uuid, out var battChars))
        {
            var level = battChars.FirstOrDefault(c => c.Uuid == FtmsUuids.BatteryLevel);
            if (level is not null)
            {
                var bytes = await ReadBytesAsync(level, "Battery Level", token).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    battery = bytes[0];
                    _log.Info(DiagnosticCategory.Gatt, $"Battery Level = {battery}%");
                }
            }
        }

        return new SensorDeviceInfo(string.IsNullOrWhiteSpace(device.Name) ? null : device.Name, systemId, firmware, hardware, manufacturer, model, battery);
    }

    /// <summary>
    /// Opens the device object with the advertised address type. The inRide uses a random static address; asking
    /// Windows for it as a public address can yield an object that never resolves to a real connection.
    /// </summary>
    private async Task<BluetoothLEDevice> GetDeviceAsync(ulong address, SensorAddressType addressType, CancellationToken token)
    {
        var order = addressType switch
        {
            SensorAddressType.Random => new[] { BluetoothAddressType.Random, BluetoothAddressType.Public },
            SensorAddressType.Public => new[] { BluetoothAddressType.Public, BluetoothAddressType.Random },
            // Unspecified (older saved sensor): guess from the address bits - random static addresses start with 0b11.
            _ => (address >> 46) == 0b11
                ? new[] { BluetoothAddressType.Random, BluetoothAddressType.Public }
                : new[] { BluetoothAddressType.Public, BluetoothAddressType.Random },
        };
        foreach (var type in order)
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, type).AsTask(token).ConfigureAwait(false);
            if (device is not null)
            {
                if (type != order[0])
                {
                    _log.Warn(DiagnosticCategory.Gatt, $"Device found only as {type} address (advertised as {addressType})");
                }
                return device;
            }
            _log.Debug(DiagnosticCategory.Gatt, $"FromBluetoothAddressAsync({DiscoveredSensor.FormatAddress(address)}, {type}) returned null");
        }
        throw new SensorConnectException("Windows could not find the device (out of range, asleep, or never seen by this PC).", deviceUnreachable: true);
    }

    private static bool Is16Bit(Guid uuid, ushort shortUuid) =>
        uuid == new Guid((uint)shortUuid, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);

    private static string Describe(Guid uuid)
    {
        var s = uuid.ToString();
        return KnownServices.TryGetValue(s[..8], out var name) ? $"{s[..8]} {name}" : s;
    }

    private static GattCharacteristic Find(IReadOnlyList<GattCharacteristic> chars, Guid uuid, string label)
    {
        foreach (var c in chars)
        {
            if (c.Uuid == uuid)
            {
                return c;
            }
        }
        var found = string.Join(", ", chars.Select(c => c.Uuid.ToString()));
        throw new SensorConnectException($"{label} characteristic {uuid} not found. Characteristics present: {found}");
    }

    private async Task<byte[]?> ReadBytesAsync(GattCharacteristic c, string label, CancellationToken ct)
    {
        try
        {
            var r = await c.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
            if (r.Status != GattCommunicationStatus.Success)
            {
                _log.Warn(DiagnosticCategory.Gatt, $"{label} read failed: {r.Status}{ProtocolError(r.ProtocolError)}");
                return null;
            }
            return ToArray(r.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn(DiagnosticCategory.Gatt, $"{label} read error: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> ReadStringAsync(GattCharacteristic c, string label, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(c, label, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }
        var s = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
        _log.Info(DiagnosticCategory.Gatt, $"{label} = '{s}'");
        return s;
    }

    internal static byte[] ToArray(IBuffer buffer)
    {
        CryptographicBuffer.CopyToByteArray(buffer, out var bytes);
        return bytes ?? [];
    }

    internal static string ProtocolError(byte? code) => code is null ? string.Empty : $" (ATT error 0x{code:X2})";

    private static void Cleanup(List<GattDeviceService> services, GattSession? session, BluetoothLEDevice? device)
    {
        foreach (var s in services)
        {
            try { s.Dispose(); } catch { /* ignored */ }
        }
        services.Clear();
        try
        {
            if (session is not null)
            {
                session.MaintainConnection = false;
                session.Dispose();
            }
        }
        catch { /* ignored */ }
        try { device?.Dispose(); } catch { /* ignored */ }
    }
}
