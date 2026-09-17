using KineticTrainer.Bluetooth.Abstractions;
using KineticTrainer.Bluetooth.Pipeline;
using KineticTrainer.Bluetooth.Session;
using KineticTrainer.Bluetooth.Simulation;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Settings;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Protocol.Ftms;
using Xunit;

namespace KineticTrainer.Tests.Bluetooth;

/// <summary>End-to-end over the simulated FTMS sensor (the protocol the physical Kinetic inRide FW 1.2 speaks).</summary>
public class FtmsSessionTests
{
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
    public async Task Ftms_sensor_connects_reads_feature_and_streams_power_cadence_speed()
    {
        var log = new DiagnosticLog();
        var hub = new TelemetryHub();
        var pipeline = new TrainerTelemetryPipeline(hub, log);
        var sim = new SimulatedSensorPlatform(log);
        sim.Profile.Protocol = TrainerProtocolKind.FitnessMachine;
        await using var session = new SensorSession([sim], pipeline, hub, log);

        await session.ConnectAsync(SimulatedSensorPlatform.SimulatedAddress, SensorAddressType.Random, null, CancellationToken.None);
        Assert.Equal(TrainerProtocolKind.FitnessMachine, session.Protocol);
        Assert.Equal(TrainerProtocolKind.FitnessMachine, pipeline.Protocol);
        Assert.Equal("inRide", session.DeviceInfo!.ModelNumber);
        Assert.Equal((byte)100, session.DeviceInfo.BatteryPercent);

        var feature = Assert.IsType<FtmsInterpreter>(pipeline.Interpreter).LatestFeature;
        Assert.NotNull(feature);
        Assert.True(feature!.SupportsSpinDown);
        Assert.Contains("Spin-down calibration: supported", pipeline.LatestConfigurationText);

        await WaitUntilAsync(() => hub.Latest?.IsDataValid == true && hub.Latest.PowerWatts > 0, TimeSpan.FromSeconds(15), "power");
        var t = hub.Latest!;
        Assert.NotNull(t.CadenceRpm);
        Assert.NotNull(t.SpeedMetersPerSecond);
        Assert.Null(t.RollerRpm);
        Assert.Equal(TrainerSensorMode.Normal, t.SensorMode);
        Assert.Equal(0, pipeline.RejectedPackets);

        // Update-rate command is an inRide-only concept.
        await Assert.ThrowsAsync<NotSupportedException>(() => session.SetUpdateRateAsync(SensorUpdateRate.TwoHz, CancellationToken.None));
    }

    [Fact]
    public async Task Ftms_spin_down_walks_the_status_sequence()
    {
        var log = new DiagnosticLog();
        var hub = new TelemetryHub();
        var pipeline = new TrainerTelemetryPipeline(hub, log);
        var sim = new SimulatedSensorPlatform(log);
        sim.Profile.Protocol = TrainerProtocolKind.FitnessMachine;
        await using var session = new SensorSession([sim], pipeline, hub, log);
        var responses = new List<string>();
        pipeline.ControlResponse += r => { lock (responses) responses.Add(r); };

        await session.ConnectAsync(SimulatedSensorPlatform.SimulatedAddress, SensorAddressType.Random, null, CancellationToken.None);

        // Without control the machine refuses.
        await session.StartSpinDownAsync(CancellationToken.None);
        await WaitUntilAsync(() => responses.Any(r => r.Contains("ControlNotPermitted")), TimeSpan.FromSeconds(15), "refusal");

        await session.RequestControlAsync(CancellationToken.None);
        await WaitUntilAsync(() => responses.Any(r => r.Contains("RequestControl -> Success")), TimeSpan.FromSeconds(15), "control granted");

        await session.StartSpinDownAsync(CancellationToken.None);
        await WaitUntilAsync(() => responses.Any(r => r.Contains("coast-down window 20.0-40.0")), TimeSpan.FromSeconds(15), "spin-down accepted");
        var interp = (FtmsInterpreter)pipeline.Interpreter;
        await WaitUntilAsync(() => interp.LatestSpinDownStatus == SpinDownStatus.StopPedaling, TimeSpan.FromSeconds(15), "stop pedaling");
        await WaitUntilAsync(() => hub.Latest?.SensorMode == TrainerSensorMode.CalibrationActive, TimeSpan.FromSeconds(15), "calibration mode in telemetry");
        await WaitUntilAsync(() => interp.LatestSpinDownStatus is null, TimeSpan.FromSeconds(20), "spin-down success");
        Assert.Contains(log.Recent(), e => e.Message.Contains("SpinDownStatus: Success"));
    }

    [Fact]
    public async Task Session_calibration_runs_the_spin_down_end_to_end()
    {
        var log = new DiagnosticLog();
        var hub = new TelemetryHub();
        var pipeline = new TrainerTelemetryPipeline(hub, log);
        var sim = new SimulatedSensorPlatform(log);
        sim.Profile.Protocol = TrainerProtocolKind.FitnessMachine;
        await using var session = new SensorSession([sim], pipeline, hub, log);
        var phases = new List<KineticTrainer.Bluetooth.Calibration.CalibrationPhase>();
        session.CalibrationChanged += s => { lock (phases) phases.Add(s.Phase); };

        await session.ConnectAsync(SimulatedSensorPlatform.SimulatedAddress, SensorAddressType.Random, null, CancellationToken.None);
        var calibration = session.StartCalibration();
        Assert.Same(calibration, session.StartCalibration()); // idempotent while active
        await WaitUntilAsync(() => !calibration.IsActive, TimeSpan.FromSeconds(30), "calibration to finish");
        Assert.Equal(KineticTrainer.Bluetooth.Calibration.CalibrationPhase.Succeeded, calibration.Phase);
        lock (phases)
        {
            Assert.Contains(KineticTrainer.Bluetooth.Calibration.CalibrationPhase.Accelerate, phases);
            Assert.Contains(KineticTrainer.Bluetooth.Calibration.CalibrationPhase.Coast, phases);
        }
        Assert.Contains("25 mph", calibration.Snapshot.TargetHighMps > 0 ? $"{calibration.Snapshot.TargetHighMps * 2.2369362920544:0} mph" : "");
    }

    [Fact]
    public async Task Malformed_ftms_packets_are_rejected_and_stream_continues()
    {
        var log = new DiagnosticLog();
        var hub = new TelemetryHub();
        var pipeline = new TrainerTelemetryPipeline(hub, log);
        var sim = new SimulatedSensorPlatform(log);
        sim.Profile.Protocol = TrainerProtocolKind.FitnessMachine;
        sim.Profile.EmitMalformedPackets = true;
        await using var session = new SensorSession([sim], pipeline, hub, log);
        await session.ConnectAsync(SimulatedSensorPlatform.SimulatedAddress, SensorAddressType.Random, null, CancellationToken.None);
        await WaitUntilAsync(() => pipeline.RejectedPackets > 0 && pipeline.AcceptedPackets > 3, TimeSpan.FromSeconds(30), "mixed packets");
        Assert.Equal(TrainerConnectionState.Connected, session.State);
        Assert.Contains(log.Recent(), e => e.Category == DiagnosticCategory.PacketRejected && e.Message.Contains("Indoor Bike Data"));
    }
}
