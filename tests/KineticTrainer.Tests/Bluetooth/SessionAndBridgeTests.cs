using System.Net.Sockets;
using System.Text.Json;
using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Bluetooth.Pipeline;
using KineticTrainer.Bluetooth.Session;
using KineticTrainer.Bluetooth.Simulation;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Settings;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Persistence;
using KineticTrainer.Protocol.InRide;
using KineticTrainer.SimulatorBridge;
using Xunit;

namespace KineticTrainer.Tests.Bluetooth;

public class PipelineTests
{
    [Fact]
    public void Accepted_packet_becomes_telemetry_and_rejected_packet_is_logged_not_thrown()
    {
        var hub = new TelemetryHub();
        var log = new DiagnosticLog();
        var pipeline = new TrainerTelemetryPipeline(hub, log);
        pipeline.UseInterpreter(new InRideInterpreter { SystemId = new byte[6] });
        using var sub = hub.Subscribe("test");

        var plain = Protocol.InRideParserTests.Plain(ticks: Protocol.InRideParserTests.TicksFor(30, 12), revs: 12, cadenceRaw: 90);
        pipeline.OnDataNotification(InRideDeobfuscator.Obfuscate(plain), DateTimeOffset.UtcNow, TrainerConnectionState.Connected);
        Assert.True(sub.Reader.TryRead(out var t));
        Assert.True(t.IsDataValid);
        Assert.True(t.PowerWatts > 0);
        Assert.Equal(30.0, t.SpeedMetersPerSecond!.Value * 3.6, 1);
        Assert.Equal(1, pipeline.AcceptedPackets);

        pipeline.OnDataNotification(new byte[7], DateTimeOffset.UtcNow, TrainerConnectionState.Connected);
        Assert.False(sub.Reader.TryRead(out _));
        Assert.Equal(1, pipeline.RejectedPackets);
        var rejected = log.Recent().Single(e => e.Category == DiagnosticCategory.PacketRejected);
        Assert.Contains("202", rejected.Message);
        Assert.Equal("00000000000000", rejected.RawHex);
    }

    [Fact]
    public void Speed_uses_configured_roller_circumference()
    {
        var hub = new TelemetryHub();
        var pipeline = new TrainerTelemetryPipeline(hub, new DiagnosticLog()) { RollerCircumferenceMeters = 0.2 };
        pipeline.UseInterpreter(new InRideInterpreter { SystemId = new byte[6] });
        var plain = Protocol.InRideParserTests.Plain(ticks: 32768, revs: 50); // 50 rev/s = 3000 rpm
        pipeline.OnDataNotification(InRideDeobfuscator.Obfuscate(plain), DateTimeOffset.UtcNow, TrainerConnectionState.Connected);
        Assert.Equal(3000, hub.Latest!.RollerRpm!.Value, 6);
        Assert.Equal(50 * 0.2, hub.Latest.SpeedMetersPerSecond!.Value, 9);
    }
}

public class SensorSessionTests
{
    private static (SensorSession Session, SimulatedSensorPlatform Sim, TelemetryHub Hub, DiagnosticLog Log) Build()
    {
        var log = new DiagnosticLog();
        var hub = new TelemetryHub();
        var pipeline = new TrainerTelemetryPipeline(hub, log);
        var sim = new SimulatedSensorPlatform(log);
        sim.Profile.Protocol = TrainerProtocolKind.InRideCustom;
        sim.Profile.UpdateRate = InRideUpdateRate.Millis250;
        var session = new SensorSession([sim], pipeline, hub, log, new SensorSessionOptions
        {
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(100),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(300),
            StaleDataTimeout = TimeSpan.FromMilliseconds(600),
            DeadLinkTimeout = TimeSpan.FromSeconds(3),
            WatchdogInterval = TimeSpan.FromMilliseconds(50),
        });
        return (session, sim, hub, log);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}");
            }
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Connect_reads_configuration_publishes_telemetry_and_remembers_sensor()
    {
        var (session, sim, hub, _) = Build();
        await using var _ = session;
        SavedSensor? saved = null;
        session.SensorConnected += s => saved = s;

        await session.ConnectAsync(SimulatedSensorPlatform.SimulatedAddress, SensorAddressType.Random, "inRide SIM", CancellationToken.None);
        Assert.Equal(TrainerConnectionState.Connected, session.State);
        Assert.NotNull(saved);
        Assert.Equal(SimulatedSensorPlatform.SimulatedAddress, saved!.BluetoothAddress);
        Assert.Equal(SensorAddressType.Random, saved.AddressType);
        Assert.Equal(Convert.ToHexStringLower(sim.Profile.SystemId), saved.SystemIdHex);
        Assert.NotNull(session.Pipeline.LatestConfigurationText);
        Assert.Equal(TrainerProtocolKind.InRideCustom, session.Protocol);

        await WaitUntilAsync(() => hub.Latest?.IsDataValid == true, TimeSpan.FromSeconds(5), "first valid telemetry");
        Assert.True(hub.Latest!.IsSimulated);
        Assert.True(session.Pipeline.AcceptedPackets > 0);
    }

    [Fact]
    public async Task Update_rate_command_is_acknowledged_in_next_packet()
    {
        var (session, _, hub, log) = Build();
        await using var _ = session;
        await session.ConnectAsync(SimulatedSensorPlatform.SimulatedAddress, SensorAddressType.Random, null, CancellationToken.None);
        await session.SetUpdateRateAsync(SensorUpdateRate.TwoHz, CancellationToken.None);
        await WaitUntilAsync(() => log.Recent().Any(e => e.Category == DiagnosticCategory.Command && e.Message.Contains("Success")),
            TimeSpan.FromSeconds(5), "command result in power packet");
        await WaitUntilAsync(() => (session.Pipeline.Interpreter as InRideInterpreter)?.LatestConfiguration?.UpdateRateDefault == InRideUpdateRate.Millis500,
            TimeSpan.FromSeconds(5), "configuration notification with new rate");
        Assert.True(hub.Latest is not null);
    }

    [Fact]
    public async Task Dropped_link_reconnects_automatically_and_user_disconnect_does_not()
    {
        var (session, sim, hub, _) = Build();
        await using var _ = session;
        var states = new List<TrainerConnectionState>();
        session.StateChanged += s => { lock (states) states.Add(s); };

        await session.ConnectAsync(SimulatedSensorPlatform.SimulatedAddress, SensorAddressType.Random, null, CancellationToken.None);
        await WaitUntilAsync(() => hub.Latest?.IsDataValid == true, TimeSpan.FromSeconds(5), "telemetry");

        sim.Profile.Unreachable = true;
        sim.ActiveLink!.SimulateDrop();
        await WaitUntilAsync(() => session.State == TrainerConnectionState.Reconnecting, TimeSpan.FromSeconds(5), "reconnecting state");
        await WaitUntilAsync(() => hub.Latest?.IsDataValid == false, TimeSpan.FromSeconds(5), "no-data sample after drop");

        await Task.Delay(500); // a few failed attempts
        sim.Profile.Unreachable = false;
        await WaitUntilAsync(() => session.State == TrainerConnectionState.Connected, TimeSpan.FromSeconds(10), "reconnected");
        await WaitUntilAsync(() => hub.Latest?.IsDataValid == true, TimeSpan.FromSeconds(5), "telemetry resumed");

        await session.DisconnectAsync();
        Assert.Equal(TrainerConnectionState.Disconnected, session.State);
        await Task.Delay(700);
        Assert.Equal(TrainerConnectionState.Disconnected, session.State);
        lock (states)
        {
            Assert.Equal([TrainerConnectionState.Connecting, TrainerConnectionState.Connected, TrainerConnectionState.Reconnecting,
                TrainerConnectionState.Connected, TrainerConnectionState.Disconnected], states);
        }
    }

    [Fact]
    public async Task Saved_sensor_is_found_when_it_appears()
    {
        var (session, sim, _, _) = Build();
        await using var _ = session;
        sim.Profile.Unreachable = true;
        await session.ReconnectToSavedAsync(new SavedSensor(SimulatedSensorPlatform.SimulatedAddress, "inRide SIM", null, DateTimeOffset.UtcNow));
        Assert.Equal(TrainerConnectionState.Searching, session.State);
        await Task.Delay(400);
        Assert.Equal(TrainerConnectionState.Searching, session.State);
        sim.Profile.Unreachable = false;
        await WaitUntilAsync(() => session.State == TrainerConnectionState.Connected, TimeSpan.FromSeconds(10), "auto-connect");
    }

    [Fact]
    public async Task Malformed_packets_from_sensor_do_not_stop_the_stream()
    {
        var (session, sim, hub, _) = Build();
        await using var _ = session;
        sim.Profile.EmitMalformedPackets = true;
        await session.ConnectAsync(SimulatedSensorPlatform.SimulatedAddress, SensorAddressType.Random, null, CancellationToken.None);
        await WaitUntilAsync(() => session.Pipeline.RejectedPackets > 0 && session.Pipeline.AcceptedPackets > 3, TimeSpan.FromSeconds(15), "mixed packets");
        Assert.Equal(TrainerConnectionState.Connected, session.State);
        Assert.True(hub.Latest!.IsDataValid);
    }
}

public class SimulatorBridgeTests
{
    private static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Streams_ndjson_to_loopback_clients_and_survives_client_disconnect()
    {
        var hub = new TelemetryHub();
        var log = new DiagnosticLog();
        await using var server = new SimulatorBridgeServer(hub, log);
        var port = FreePort();
        Assert.True(server.Start(port));

        using var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        using var reader = new StreamReader(client.GetStream());
        var hello = await reader.ReadLineAsync();
        Assert.Contains("\"type\":\"hello\"", hello);

        hub.Publish(new TrainerTelemetry
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1789500000000),
            ConnectionState = TrainerConnectionState.Connected,
            IsDataValid = true,
            PowerWatts = 210,
            CadenceRpm = 88,
            SpeedMetersPerSecond = 8.42,
            RollerRpm = 2978.2,
        });
        var line = await reader.ReadLineAsync();
        using var doc = JsonDocument.Parse(line!);
        Assert.Equal(210, doc.RootElement.GetProperty("power").GetInt32());
        Assert.Equal(88, doc.RootElement.GetProperty("cadence").GetDouble());
        Assert.Equal(8.42, doc.RootElement.GetProperty("speedMps").GetDouble());
        Assert.Equal(1789500000000, doc.RootElement.GetProperty("timestamp").GetInt64());
        Assert.Equal("connected", doc.RootElement.GetProperty("connection").GetString());

        client.Close();
        await Task.Delay(200);
        hub.Publish(TrainerTelemetry.NoData(DateTimeOffset.UtcNow, TrainerConnectionState.Connected));
        Assert.True(server.IsRunning);

        // A second client gets the latest sample immediately on connect.
        using var client2 = new TcpClient();
        await client2.ConnectAsync(System.Net.IPAddress.Loopback, port);
        using var reader2 = new StreamReader(client2.GetStream());
        await reader2.ReadLineAsync();
        var latest = await reader2.ReadLineAsync();
        Assert.Contains("\"valid\":false", latest);
    }

    [Fact]
    public void Port_conflict_is_reported_not_thrown()
    {
        var port = FreePort();
        var blocker = new TcpListener(System.Net.IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            var server = new SimulatorBridgeServer(new TelemetryHub(), new DiagnosticLog());
            Assert.False(server.Start(port));
            Assert.NotNull(server.LastError);
            Assert.False(server.IsRunning);
        }
        finally
        {
            blocker.Stop();
        }
    }
}

public class PersistenceTests
{
    [Fact]
    public async Task Settings_round_trip_and_schema_version()
    {
        var path = Path.Combine(Path.GetTempPath(), "kt-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var db = new LocalDatabase(path);
            await db.InitializeAsync();
            Assert.Equal(LocalDatabase.LatestSchemaVersion, await db.GetSchemaVersionAsync());
            var store = new SettingsStore(db);
            Assert.Equal(new AppSettings(), await store.LoadAsync());
            var s = new AppSettings
            {
                RollerCircumferenceMeters = 0.17,
                SimulatorBridgePort = 50000,
                PreferredUpdateRate = SensorUpdateRate.FourHz,
                LastSensor = new SavedSensor(0xAABBCCDDEEFF, "inRide", "0123456789ab", DateTimeOffset.UnixEpoch, SensorAddressType.Random),
            };
            await store.SaveAsync(s);
            var loaded = await store.LoadAsync();
            Assert.Equal(s, loaded);
            await db.InitializeAsync(); // idempotent
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
