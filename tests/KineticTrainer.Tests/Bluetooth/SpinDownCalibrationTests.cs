using KineticTrainer.Bluetooth.Calibration;
using KineticTrainer.Protocol.Ftms;
using Xunit;

namespace KineticTrainer.Tests.Bluetooth;

public class SpinDownCalibrationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static FtmsControlPointResponse Response(FtmsOpCode op, FtmsResultCode result, byte[]? param = null)
    {
        var bytes = new List<byte> { 0x80, (byte)op, (byte)result };
        if (param is not null)
        {
            bytes.AddRange(param);
        }
        return FtmsControlPoint.ParseResponse(bytes.ToArray()).Value!;
    }

    private static FitnessMachineStatusEvent Status(SpinDownStatus s) => FitnessMachineStatusEvent.Parse([0x14, (byte)s]).Value!;

    [Fact]
    public void Happy_path_walks_idle_ready_active_success_and_requests_the_right_writes()
    {
        var c = new SpinDownCalibration();
        var writes = new List<string>();
        var phases = new List<CalibrationPhase>();
        c.CommandRequested += b => writes.Add(Convert.ToHexString(b));
        c.Changed += s => phases.Add(s.Phase);

        c.Begin(T0);
        Assert.Equal(["00"], writes);
        Assert.Equal(CalibrationPhase.RequestingControl, c.Phase);

        c.OnControlResponse(Response(FtmsOpCode.RequestControl, FtmsResultCode.Success), T0.AddSeconds(1));
        Assert.Equal(["00", "1301"], writes);
        Assert.Equal(CalibrationPhase.Starting, c.Phase);

        // 20.00 .. 40.00 km/h window from the sensor
        c.OnControlResponse(Response(FtmsOpCode.SpinDownControl, FtmsResultCode.Success, [0xD0, 0x07, 0xA0, 0x0F]), T0.AddSeconds(2));
        Assert.Equal(CalibrationPhase.Accelerate, c.Phase);
        Assert.Equal(40.0 / 3.6, c.Snapshot.TargetHighMps, 6);
        Assert.Contains("25 mph", c.Snapshot.Instruction); // 40 km/h

        c.OnSpeed(5.0, T0.AddSeconds(10));
        Assert.Contains("Now 11 mph", c.Snapshot.Detail);
        c.OnSpeed(12.0, T0.AddSeconds(20)); // above 11.1 m/s target
        Assert.StartsWith("Keep pedalling", c.Snapshot.Instruction);

        c.OnStatus(Status(SpinDownStatus.StopPedaling), T0.AddSeconds(21));
        Assert.Equal(CalibrationPhase.Coast, c.Phase);
        Assert.StartsWith("STOP PEDALLING", c.Snapshot.Instruction);

        c.OnStatus(Status(SpinDownStatus.Success), T0.AddSeconds(30));
        Assert.Equal(CalibrationPhase.Succeeded, c.Phase);
        Assert.False(c.IsActive);
        Assert.Contains("successful", c.Snapshot.Result);
        Assert.Equal([CalibrationPhase.RequestingControl, CalibrationPhase.Starting, CalibrationPhase.Accelerate, CalibrationPhase.Accelerate,
            CalibrationPhase.Accelerate, CalibrationPhase.Coast, CalibrationPhase.Succeeded], phases);
    }

    [Fact]
    public void Status_requested_can_arrive_before_or_instead_of_the_control_response()
    {
        var c = new SpinDownCalibration();
        c.Begin(T0);
        c.OnControlResponse(Response(FtmsOpCode.RequestControl, FtmsResultCode.Success), T0);
        c.OnStatus(Status(SpinDownStatus.SpinDownRequested), T0);
        Assert.Equal(CalibrationPhase.Accelerate, c.Phase);
        // A later 3-byte success response without a window keeps the defaults.
        c.OnControlResponse(Response(FtmsOpCode.SpinDownControl, FtmsResultCode.Success), T0);
        Assert.Equal(CalibrationPhase.Accelerate, c.Phase);
        Assert.Equal(SpinDownCalibration.DefaultTargetHighKph / 3.6, c.Snapshot.TargetHighMps, 6);
    }

    [Fact]
    public void Refusals_fail_with_the_result_code()
    {
        var c = new SpinDownCalibration();
        c.Begin(T0);
        c.OnControlResponse(Response(FtmsOpCode.RequestControl, FtmsResultCode.ControlNotPermitted), T0);
        Assert.Equal(CalibrationPhase.Failed, c.Phase);
        Assert.Contains("ControlNotPermitted", c.Snapshot.Result);

        var d = new SpinDownCalibration();
        d.Begin(T0);
        d.OnControlResponse(Response(FtmsOpCode.RequestControl, FtmsResultCode.Success), T0);
        d.OnControlResponse(Response(FtmsOpCode.SpinDownControl, FtmsResultCode.OpCodeNotSupported), T0);
        Assert.Equal(CalibrationPhase.Failed, d.Phase);
        Assert.Contains("OpCodeNotSupported", d.Snapshot.Result);
    }

    [Fact]
    public void Sensor_error_and_lost_control_fail_with_guidance()
    {
        var c = new SpinDownCalibration();
        c.Begin(T0);
        c.OnControlResponse(Response(FtmsOpCode.RequestControl, FtmsResultCode.Success), T0);
        c.OnControlResponse(Response(FtmsOpCode.SpinDownControl, FtmsResultCode.Success), T0);
        c.OnStatus(Status(SpinDownStatus.StopPedaling), T0);
        c.OnStatus(Status(SpinDownStatus.Error), T0);
        Assert.Equal(CalibrationPhase.Failed, c.Phase);
        Assert.Contains("stop pedalling", c.Snapshot.Result);

        var d = new SpinDownCalibration();
        d.Begin(T0);
        d.OnStatus(FitnessMachineStatusEvent.Parse([0xFF]).Value!, T0);
        Assert.Equal(CalibrationPhase.Failed, d.Phase);
        Assert.Contains("withdrew control", d.Snapshot.Result);
    }

    [Fact]
    public void Timeouts_apply_per_phase()
    {
        var c = new SpinDownCalibration { ControlTimeout = TimeSpan.FromSeconds(5), AccelerateTimeout = TimeSpan.FromSeconds(60), CoastTimeout = TimeSpan.FromSeconds(30) };
        c.Begin(T0);
        c.Tick(T0.AddSeconds(4));
        Assert.Equal(CalibrationPhase.RequestingControl, c.Phase);
        c.Tick(T0.AddSeconds(6));
        Assert.Equal(CalibrationPhase.Failed, c.Phase);
        Assert.Contains("control request", c.Snapshot.Result);

        var d = new SpinDownCalibration { AccelerateTimeout = TimeSpan.FromSeconds(60) };
        d.Begin(T0);
        d.OnControlResponse(Response(FtmsOpCode.RequestControl, FtmsResultCode.Success), T0);
        d.OnControlResponse(Response(FtmsOpCode.SpinDownControl, FtmsResultCode.Success), T0.AddSeconds(1));
        d.Tick(T0.AddSeconds(59));
        Assert.Equal(CalibrationPhase.Accelerate, d.Phase);
        d.Tick(T0.AddSeconds(62));
        Assert.Equal(CalibrationPhase.Failed, d.Phase);
        Assert.Contains("target speed", d.Snapshot.Result);
    }

    [Fact]
    public void Cancel_and_events_after_completion_are_ignored()
    {
        var c = new SpinDownCalibration();
        c.Cancel(T0); // nothing running
        Assert.Equal(CalibrationPhase.Idle, c.Phase);
        c.Begin(T0);
        c.Cancel(T0);
        Assert.Equal(CalibrationPhase.Cancelled, c.Phase);
        c.OnStatus(Status(SpinDownStatus.Success), T0);
        Assert.Equal(CalibrationPhase.Cancelled, c.Phase);
        // Begin again is allowed after a terminal phase.
        c.Begin(T0);
        Assert.Equal(CalibrationPhase.RequestingControl, c.Phase);
    }

    [Fact]
    public void Speed_formatter_is_used_for_instructions()
    {
        var c = new SpinDownCalibration { FormatSpeed = mps => $"{mps * 3.6:0} km/h" };
        c.Begin(T0);
        c.OnControlResponse(Response(FtmsOpCode.RequestControl, FtmsResultCode.Success), T0);
        c.OnControlResponse(Response(FtmsOpCode.SpinDownControl, FtmsResultCode.Success), T0);
        Assert.Equal("Pedal up to 34 km/h", c.Snapshot.Instruction);
    }
}
