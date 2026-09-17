// BleProbe: scan for a BLE device, connect, and dump every service / characteristic / descriptor,
// read readable values, and capture a few notifications from notify-capable characteristics.
//
//   dotnet run --project tools\BleProbe -- [--target <hex-address | name-substring>] [--scan 15] [--wait 180]
//                                          [--notify-seconds 12] [--all-notify]
//
// Default target: the first device whose name contains "inride"/"kinetic" or that advertises E9410100.
using System.Diagnostics;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

var kineticPower = Guid.Parse("E9410100-B434-446B-B5CC-36592FC4C724");
string? target = null;
var scanSeconds = 15;
var waitSeconds = 180;
var notifySeconds = 12;
var allNotify = false;
var ftmsStart = false;
var ftmsSpindown = false;
var ftmsExperiments = false;
var unpairOnly = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--target": target = args[++i]; break;
        case "--scan": scanSeconds = int.Parse(args[++i]); break;
        case "--wait": waitSeconds = int.Parse(args[++i]); break;
        case "--notify-seconds": notifySeconds = int.Parse(args[++i]); break;
        case "--all-notify": allNotify = true; break;
        case "--ftms-start": ftmsStart = true; break;   // write Request Control + Start/Resume before listening to Indoor Bike Data
        case "--ftms-spindown": ftmsSpindown = true; break; // write Request Control + Spin Down Start, then watch status + data
        case "--ftms-experiments": ftmsExperiments = true; break; // staged: baseline, control ops one by one, then bond; watch for any byte change
        case "--unpair": unpairOnly = true; break;               // just report pairing state and unpair if paired
    }
}

Console.OutputEncoding = Encoding.UTF8;
void P(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");
static string Hex(IBuffer b) { CryptographicBuffer.CopyToByteArray(b, out var a); return a is null ? "" : Convert.ToHexString(a).ToLowerInvariant(); }
static string Ascii(IBuffer b) { CryptographicBuffer.CopyToByteArray(b, out var a); return a is null ? "" : new string(a.Select(c => c is >= 32 and < 127 ? (char)c : '.').ToArray()); }
static string Addr(ulong a) => string.Join(':', Enumerable.Range(0, 6).Select(i => ((a >> (8 * (5 - i))) & 0xFF).ToString("X2")));

ulong? targetAddress = null;
if (target is not null && target.Replace(":", "").Length == 12 && target.Replace(":", "").All(Uri.IsHexDigit))
{
    targetAddress = Convert.ToUInt64(target.Replace(":", ""), 16);
}

// ---------------------------------------------------------------- 1. adapter
var adapter = await BluetoothAdapter.GetDefaultAsync();
P(adapter is null ? "NO BLUETOOTH ADAPTER" : $"Adapter: address={Addr(adapter.BluetoothAddress)} LE={adapter.IsLowEnergySupported} central={adapter.IsCentralRoleSupported} " +
                                              $"extAdv={adapter.IsExtendedAdvertisingSupported} maxAdv={adapter.MaxAdvertisementDataLength}");
var radio = adapter is null ? null : await adapter.GetRadioAsync();
P($"Radio: {radio?.Name} state={radio?.State}");

// ---------------------------------------------------------------- 1b. --unpair: check Windows' bond list first (works while the sensor sleeps)
if (unpairOnly)
{
    var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
    P($"Paired Bluetooth LE devices in Windows: {paired.Count}");
    var removed = 0;
    foreach (var d in paired)
    {
        var match = d.Name.Contains("inride", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("kinetic", StringComparison.OrdinalIgnoreCase)
                    || (targetAddress is { } ta && d.Id.EndsWith(Addr(ta).Replace(":", "").ToLowerInvariant().Insert(10, ":").Insert(8, ":").Insert(6, ":").Insert(4, ":").Insert(2, ":"), StringComparison.OrdinalIgnoreCase));
        P($"  '{d.Name}' id={d.Id}{(match ? "  <-- our sensor" : "")}");
        if (match)
        {
            var r = await d.Pairing.UnpairAsync();
            P($"    UnpairAsync -> {r.Status}");
            removed++;
        }
    }
    P(removed == 0 ? "Sensor is NOT paired in Windows; nothing to undo." : $"Removed {removed} bond(s).");
    return 0;
}

// ---------------------------------------------------------------- 2. scan (repeat until target seen or --wait elapses)
var seen = new Dictionary<ulong, (string name, BluetoothAddressType type, HashSet<Guid> svcs, short rssi, int count, string extra)>();
ulong? found = null;
BluetoothAddressType foundType = BluetoothAddressType.Random;
var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
while (found is null && DateTime.UtcNow < deadline)
{
    seen.Clear();
    var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
    watcher.Received += (_, e) =>
    {
        lock (seen)
        {
            var name = e.Advertisement.LocalName;
            var svcs = e.Advertisement.ServiceUuids.ToHashSet();
            var extra = new StringBuilder();
            foreach (var md in e.Advertisement.ManufacturerData) extra.Append($" mfr=0x{md.CompanyId:X4}:{Hex(md.Data)}");
            foreach (var ds in e.Advertisement.DataSections) extra.Append($" ds=0x{ds.DataType:X2}:{Hex(ds.Data)}");
            extra.Append($" type={e.AdvertisementType} flags={e.Advertisement.Flags}");
            if (seen.TryGetValue(e.BluetoothAddress, out var prev))
            {
                if (string.IsNullOrEmpty(name)) name = prev.name;
                svcs.UnionWith(prev.svcs);
                seen[e.BluetoothAddress] = (name, e.BluetoothAddressType, svcs, e.RawSignalStrengthInDBm, prev.count + 1, prev.extra + " |" + extra);
            }
            else
            {
                seen[e.BluetoothAddress] = (name ?? "", e.BluetoothAddressType, svcs, e.RawSignalStrengthInDBm, 1, extra.ToString());
            }
        }
    };
    watcher.Start();
    P($"Scanning {scanSeconds}s…");
    await Task.Delay(TimeSpan.FromSeconds(scanSeconds));
    watcher.Stop();
    lock (seen)
    {
        foreach (var (addr, d) in seen.OrderByDescending(kv => kv.Value.rssi))
        {
            var interesting = d.name.Contains("inride", StringComparison.OrdinalIgnoreCase) || d.name.Contains("kinetic", StringComparison.OrdinalIgnoreCase) || d.svcs.Contains(kineticPower);
            if (interesting || targetAddress == addr || (target is not null && targetAddress is null && d.name.Contains(target, StringComparison.OrdinalIgnoreCase)))
            {
                P($"  {Addr(addr)} addrType={d.type} rssi={d.rssi} n={d.count} name='{d.name}' services=[{string.Join(",", d.svcs)}]{d.extra}");
            }
            var isTarget = targetAddress is not null ? targetAddress == addr
                : target is not null ? d.name.Contains(target, StringComparison.OrdinalIgnoreCase)
                : interesting;
            if (isTarget && found is null)
            {
                found = addr;
                foundType = d.type;
            }
        }
        P($"  ({seen.Count} advertisers total)");
    }
    if (found is null) P("Target not seen yet - spin the trainer roller to wake the sensor…");
}
if (found is null) { P("GIVING UP: target never advertised."); return 2; }
P($"TARGET {Addr(found.Value)} addressType={foundType}");

// ---------------------------------------------------------------- 3. connect + enumerate (try observed address type first, then the other)
foreach (var addrType in new[] { foundType, foundType == BluetoothAddressType.Random ? BluetoothAddressType.Public : BluetoothAddressType.Random })
{
    P($"=== FromBluetoothAddressAsync({Addr(found.Value)}, {addrType})");
    BluetoothLEDevice? dev;
    try { dev = await BluetoothLEDevice.FromBluetoothAddressAsync(found.Value, addrType); }
    catch (Exception ex) { P($"  threw {ex.GetType().Name}: {ex.Message}"); continue; }
    if (dev is null) { P("  -> null (Windows has no device object for this address/type)"); continue; }
    using (dev)
    {
        P($"  Name='{dev.Name}' id={dev.DeviceId} status={dev.ConnectionStatus} appearance={dev.Appearance?.Category}/{dev.Appearance?.SubCategory} " +
          $"paired={dev.DeviceInformation.Pairing.IsPaired} canPair={dev.DeviceInformation.Pairing.CanPair} access={dev.DeviceAccessInformation.CurrentStatus} addrType={dev.BluetoothAddressType}");
        if (unpairOnly)
        {
            if (dev.DeviceInformation.Pairing.IsPaired)
            {
                var un = await dev.DeviceInformation.Pairing.UnpairAsync();
                P($"  UnpairAsync -> {un.Status}");
            }
            else
            {
                P("  not paired; nothing to do");
            }
            return 0;
        }
        dev.ConnectionStatusChanged += (s, _) => P($"  [event] ConnectionStatus -> {s.ConnectionStatus}");
        dev.GattServicesChanged += (_, _) => P("  [event] GattServicesChanged");

        GattSession? session = null;
        try
        {
            session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
            session.MaintainConnection = true;
            session.SessionStatusChanged += (_, e) => P($"  [event] GattSession -> {e.Status} err={e.Error}");
            P($"  GattSession: status={session.SessionStatus} canMaintain={session.CanMaintainConnection} mtu={session.MaxPduSize}");
        }
        catch (Exception ex) { P($"  GattSession failed: {ex.Message}"); }

        var access = await dev.RequestAccessAsync();
        P($"  RequestAccessAsync -> {access}");

        GattDeviceServicesResult? svcResult = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var sw = Stopwatch.StartNew();
            svcResult = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            P($"  GetGattServicesAsync(Uncached) attempt {attempt}: status={svcResult.Status} attErr={svcResult.ProtocolError?.ToString("X2") ?? "-"} count={svcResult.Services.Count} in {sw.ElapsedMilliseconds}ms; device status={dev.ConnectionStatus}");
            if (svcResult.Status == GattCommunicationStatus.Success && svcResult.Services.Count > 0) break;
            var cached = await dev.GetGattServicesAsync(BluetoothCacheMode.Cached);
            P($"  GetGattServicesAsync(Cached): status={cached.Status} count={cached.Services.Count}");
            var byUuid = await dev.GetGattServicesForUuidAsync(kineticPower, BluetoothCacheMode.Uncached);
            P($"  GetGattServicesForUuidAsync(E9410100, Uncached): status={byUuid.Status} count={byUuid.Services.Count}");
            await Task.Delay(3000);
        }
        if (svcResult is null || svcResult.Services.Count == 0) { P("  NO SERVICES FOUND with this address type."); session?.Dispose(); continue; }

        var notifyTargets = new List<GattCharacteristic>();
        foreach (var svc in svcResult.Services)
        {
            P($"  SERVICE {svc.Uuid} handle=0x{svc.AttributeHandle:X4}");
            var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            P($"    characteristics: status={chars.Status} attErr={chars.ProtocolError?.ToString("X2") ?? "-"} count={chars.Characteristics.Count}");
            foreach (var c in chars.Characteristics)
            {
                var desc = "";
                try
                {
                    var descs = await c.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
                    if (descs.Status == GattCommunicationStatus.Success && descs.Descriptors.Count > 0)
                        desc = " descriptors=[" + string.Join(",", descs.Descriptors.Select(d => d.Uuid.ToString()[..8])) + "]";
                }
                catch { /* ignore */ }
                var value = "";
                if (c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                {
                    try
                    {
                        var r = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
                        value = r.Status == GattCommunicationStatus.Success ? $" value={Hex(r.Value)} '{Ascii(r.Value)}' ({r.Value.Length} bytes)" : $" read={r.Status}/{r.ProtocolError?.ToString("X2")}";
                    }
                    catch (Exception ex) { value = $" read threw {ex.Message}"; }
                }
                P($"    CHAR {c.Uuid} handle=0x{c.AttributeHandle:X4} props={c.CharacteristicProperties} userDesc='{c.UserDescription}'{desc}{value}");
                if (c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) || c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
                {
                    if (allNotify || svc.Uuid == kineticPower || svc.Uuid.ToString().StartsWith("00001818") || svc.Uuid.ToString().StartsWith("00001816"))
                        notifyTargets.Add(c);
                }
            }
        }

        // ------------------------------------------------------------ 3a. staged experiments: what does the sensor need before it reports?
        if (ftmsExperiments)
        {
            var ftmsSvc = svcResult.Services.FirstOrDefault(s => s.Uuid.ToString().StartsWith("00001826"));
            if (ftmsSvc is null) { P("  no FTMS service"); return 3; }
            var fch = (await ftmsSvc.GetCharacteristicsAsync(BluetoothCacheMode.Cached)).Characteristics;
            var ibd = fch.First(c => c.Uuid.ToString().StartsWith("00002ad2"));
            var cp = fch.First(c => c.Uuid.ToString().StartsWith("00002ad9"));
            var st = fch.First(c => c.Uuid.ToString().StartsWith("00002ada"));
            var distinct = new SortedDictionary<string, int>();
            var total = 0;
            var lastHex = "";
            void DataHandler(GattCharacteristic s, GattValueChangedEventArgs e)
            {
                var hex = Hex(e.CharacteristicValue);
                lock (distinct) { distinct[hex] = distinct.TryGetValue(hex, out var n) ? n + 1 : 1; total++; }
                if (hex != lastHex) P($"    DATA #{total} {hex}   <-- {(lastHex.Length == 0 ? "first" : "CHANGED")}");
                lastHex = hex;
            }
            cp.ValueChanged += (s, e) => P($"    CP-INDICATE {Hex(e.CharacteristicValue)}");
            st.ValueChanged += (s, e) => P($"    STATUS {Hex(e.CharacteristicValue)}");
            P($"  subscribe status -> {(await st.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)).Status}");
            P($"  subscribe control point -> {(await cp.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate)).Status}");
            ibd.ValueChanged += DataHandler;
            P($"  subscribe Indoor Bike Data -> {(await ibd.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)).Status}");

            async Task StageAsync(string label, byte[]? write, int seconds)
            {
                if (write is not null)
                {
                    try
                    {
                        var w = await cp.WriteValueWithResultAsync(CryptographicBuffer.CreateFromByteArray(write), GattWriteOption.WriteWithResponse);
                        P($"  WRITE {label} [{Convert.ToHexString(write)}] -> {w.Status} attErr={w.ProtocolError?.ToString("X2") ?? "-"}");
                    }
                    catch (Exception ex) { P($"  WRITE {label} threw {ex.Message}"); }
                }
                lock (distinct) distinct.Clear();
                P($"  --- stage '{label}': watching {seconds}s (keep pedalling) ---");
                await Task.Delay(seconds * 1000);
                string summary;
                lock (distinct) summary = string.Join(", ", distinct.Select(kv => $"{kv.Key} x{kv.Value}"));
                P($"  --- stage '{label}' result: {summary} ---");
            }

            await StageAsync("baseline (no writes)", null, 20);
            await StageAsync("Request Control", new byte[] { 0x00 }, 15);
            await StageAsync("Set Indoor Bike Simulation 0 wind / 0 grade", new byte[] { 0x11, 0x00, 0x00, 0x00, 0x00, 0x21, 0x28 }, 15);
            await StageAsync("Set Target Power 100 W", new byte[] { 0x05, 0x64, 0x00 }, 15);
            await StageAsync("Reset", new byte[] { 0x01 }, 15);
            await StageAsync("Spin Down Ignore", new byte[] { 0x13, 0x02 }, 15);
            await StageAsync("Set Wheel Circumference 2096 mm", new byte[] { 0x12, 0xE0, 0x51 }, 15);

            // Last resort: bond (Just Works). Phones bond silently when a peripheral asks; Windows never does on its own.
            var pairing = dev.DeviceInformation.Pairing;
            P($"  pairing: isPaired={pairing.IsPaired} canPair={pairing.CanPair}");
            if (!pairing.IsPaired && pairing.CanPair)
            {
                var custom = pairing.Custom;
                custom.PairingRequested += (s, e) =>
                {
                    P($"  pairing requested: kind={e.PairingKind} pin={e.Pin}");
                    if (e.PairingKind == DevicePairingKinds.ProvidePin) e.Accept("000000"); else e.Accept();
                };
                var pr = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ProvidePin,
                    DevicePairingProtectionLevel.None);
                P($"  PairAsync -> {pr.Status} protection={pr.ProtectionLevelUsed}");
                await Task.Delay(3000);
                try
                {
                    P($"  re-subscribe Indoor Bike Data -> {(await ibd.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)).Status}");
                }
                catch (Exception ex) { P($"  re-subscribe threw {ex.Message}"); }
                await StageAsync("after bonding", null, 20);
                var un = await dev.DeviceInformation.Pairing.UnpairAsync();
                P($"  UnpairAsync -> {un.Status}");
            }
            ibd.ValueChanged -= DataHandler;
            P($"  EXPERIMENTS DONE: {total} data packets total");
            foreach (var svc in svcResult.Services) svc.Dispose();
            session?.Dispose();
            return 0;
        }

        // ------------------------------------------------------------ 3b. optional FTMS control sequence
        if (ftmsStart || ftmsSpindown)
        {
            var ftmsSvc = svcResult.Services.FirstOrDefault(s => s.Uuid.ToString().StartsWith("00001826"));
            var cp = ftmsSvc is null ? null : (await ftmsSvc.GetCharacteristicsAsync(BluetoothCacheMode.Cached)).Characteristics.FirstOrDefault(c => c.Uuid.ToString().StartsWith("00002ad9"));
            var st = ftmsSvc is null ? null : (await ftmsSvc.GetCharacteristicsAsync(BluetoothCacheMode.Cached)).Characteristics.FirstOrDefault(c => c.Uuid.ToString().StartsWith("00002ada"));
            if (cp is null) { P("  --ftms-start: no control point found"); }
            else
            {
                cp.ValueChanged += (s, e) => P($"    CP-INDICATE {Hex(e.CharacteristicValue)}");
                if (st is not null)
                {
                    st.ValueChanged += (s, e) => P($"    STATUS {Hex(e.CharacteristicValue)}");
                    var stSub = await st.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    P($"  subscribe Fitness Machine Status -> {stSub.Status}");
                }
                var sub = await cp.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate);
                P($"  subscribe Control Point indications -> {sub.Status}");
                var writes = new List<(string, byte[])> { ("Request Control", new byte[] { 0x00 }) };
                if (ftmsStart) writes.Add(("Start/Resume", new byte[] { 0x07 }));
                if (ftmsSpindown) writes.Add(("Spin Down Control: Start", new byte[] { 0x13, 0x01 }));
                foreach (var (label, payload) in writes)
                {
                    var w = await cp.WriteValueWithResultAsync(CryptographicBuffer.CreateFromByteArray(payload), GattWriteOption.WriteWithResponse);
                    P($"  WRITE {label} {Convert.ToHexString(payload)} -> {w.Status} attErr={w.ProtocolError?.ToString("X2") ?? "-"}");
                    await Task.Delay(1500);
                }
                if (ftmsSpindown) P("  >>> RIDER: pedal up to ~21 mph, then stop pedalling and coast until the roller stops <<<");
                // Keep only Indoor Bike Data in the listen list so the whole window goes to it.
                notifyTargets = notifyTargets.Where(c => c.Uuid.ToString().StartsWith("00002ad2")).ToList();
            }
        }

        // ------------------------------------------------------------ 4. notifications
        foreach (var c in notifyTargets)
        {
            var count = 0;
            var lastHex = "";
            void Handler(GattCharacteristic s, GattValueChangedEventArgs e)
            {
                var n = Interlocked.Increment(ref count);
                var hex = Hex(e.CharacteristicValue);
                // Print every change immediately, otherwise one line per 20 identical packets.
                if (hex != lastHex || n % 20 == 0 || n <= 2)
                {
                    P($"    NOTIFY {s.Uuid.ToString()[..8]} #{n} {e.CharacteristicValue.Length}B {hex}{(hex != lastHex && n > 1 ? "   <-- CHANGED" : "")}");
                }
                lastHex = hex;
            }
            c.ValueChanged += Handler;
            var mode = c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ? GattClientCharacteristicConfigurationDescriptorValue.Notify : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            var cccd = await c.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(mode);
            P($"  SUBSCRIBE {c.Uuid} ({mode}) -> {cccd.Status} attErr={cccd.ProtocolError?.ToString("X2") ?? "-"}; listening {notifySeconds}s (spin the roller / pedal)");
            await Task.Delay(TimeSpan.FromSeconds(notifySeconds));
            c.ValueChanged -= Handler;
            P($"  {count} notifications received from {c.Uuid.ToString()[..8]}");
            try { await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); } catch { /* ignore */ }
        }

        foreach (var svc in svcResult.Services) svc.Dispose();
        session?.Dispose();
        P("=== done with this address type");
        return 0;
    }
}
P("FINISHED without a successful enumeration.");
return 1;
