using KineticTrainer.Protocol.Ftms;

namespace KineticTrainer.Bluetooth.Calibration;

/// <summary>Rider-facing phases of a spin-down. Maps Kinetic's Idle -> Ready -> Active -> Normal onto FTMS events.</summary>
public enum CalibrationPhase
{
    Idle,
    /// <summary>Request Control written; waiting for the sensor to grant it.</summary>
    RequestingControl,
    /// <summary>Spin Down Start written; waiting for the sensor to acknowledge.</summary>
    Starting,
    /// <summary>Rider must pedal up past the target speed (Kinetic "Idle": accelerate to over ~20 mph).</summary>
    Accelerate,
    /// <summary>Rider must stop pedalling and coast (Kinetic "Ready"/"Active": the sensor times the coast-down).</summary>
    Coast,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record CalibrationSnapshot(
    CalibrationPhase Phase,
    string Instruction,
    string Detail,
    double TargetHighMps,
    double TargetLowMps,
    double? SpeedMps,
    bool IsActive,
    string? Result);

/// <summary>
/// The spin-down (calibration) state machine for FTMS trainers, kept free of UI and BLE so it can be unit tested.
/// The owner performs the control-point writes it asks for through <see cref="CommandRequested"/> and feeds back
/// control-point responses, Fitness Machine Status events, live speed and a clock tick.
///
/// Sequence on the wire: Request Control (0x00) -> 0x80 00 01; Spin Down Control Start (0x13 0x01) -> 0x80 13 01
/// [targetLow targetHigh]; status 0x14/0x01 Spin Down Requested; rider accelerates; status 0x14/0x04 Stop Pedaling;
/// rider coasts; status 0x14/0x02 Success or 0x14/0x03 Error.
/// </summary>
public sealed class SpinDownCalibration
{
    /// <summary>Kinetic's guidance: "speed up to 21 mph, then stop pedalling and coast".</summary>
    public const double DefaultTargetHighKph = 34.0;

    /// <summary>End of the timed coast-down window (~13 mph on Kinetic's older sensor).</summary>
    public const double DefaultTargetLowKph = 21.0;

    private readonly object _gate = new();
    private CalibrationPhase _phase = CalibrationPhase.Idle;
    private double _highMps = DefaultTargetHighKph / 3.6;
    private double _lowMps = DefaultTargetLowKph / 3.6;
    private double? _speedMps;
    private DateTimeOffset _phaseSince;
    private string? _result;
    private bool _reachedTarget;

    public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan AccelerateTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan CoastTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Formats a speed in m/s for the rider (default: whole mph).</summary>
    public Func<double, string> FormatSpeed { get; set; } = mps => $"{mps * 2.2369362920544:0} mph";

    public event Action<CalibrationSnapshot>? Changed;

    /// <summary>Bytes the owner must write to the control point (write with response).</summary>
    public event Action<byte[]>? CommandRequested;

    public CalibrationPhase Phase
    {
        get { lock (_gate) return _phase; }
    }

    public bool IsActive
    {
        get { lock (_gate) return IsActivePhase(_phase); }
    }

    public CalibrationSnapshot Snapshot
    {
        get { lock (_gate) return Build(); }
    }

    private static bool IsActivePhase(CalibrationPhase p) =>
        p is CalibrationPhase.RequestingControl or CalibrationPhase.Starting or CalibrationPhase.Accelerate or CalibrationPhase.Coast;

    public void Begin(DateTimeOffset now)
    {
        byte[]? command = null;
        lock (_gate)
        {
            if (IsActivePhase(_phase))
            {
                return;
            }
            _result = null;
            _reachedTarget = false;
            SetPhase(CalibrationPhase.RequestingControl, now);
            command = FtmsControlPoint.RequestControl();
        }
        Notify();
        CommandRequested?.Invoke(command);
    }

    public void OnControlResponse(FtmsControlPointResponse response, DateTimeOffset now)
    {
        byte[]? command = null;
        lock (_gate)
        {
            switch (_phase)
            {
                case CalibrationPhase.RequestingControl when response.RequestOpCode == FtmsOpCode.RequestControl:
                    if (response.IsSuccess)
                    {
                        SetPhase(CalibrationPhase.Starting, now);
                        command = FtmsControlPoint.StartSpinDown();
                    }
                    else
                    {
                        FailLocked($"The sensor refused control ({response.Result}).", now);
                    }
                    break;
                // The status "Spin Down Requested" may arrive before the control-point acknowledgement, so the
                // acknowledgement (which carries the speed window) is honoured in any later active phase too.
                case CalibrationPhase.Starting or CalibrationPhase.Accelerate or CalibrationPhase.Coast when response.RequestOpCode == FtmsOpCode.SpinDownControl:
                    if (response.IsSuccess)
                    {
                        if (response.SpinDownTargetSpeedLowKph is { } low && response.SpinDownTargetSpeedHighKph is { } high && high > 0)
                        {
                            _lowMps = low / 3.6;
                            _highMps = high / 3.6;
                        }
                        if (_phase == CalibrationPhase.Starting)
                        {
                            SetPhase(CalibrationPhase.Accelerate, now);
                        }
                    }
                    else
                    {
                        FailLocked($"The sensor refused to start a spin-down ({response.Result}).", now);
                    }
                    break;
                default:
                    return;
            }
        }
        Notify();
        if (command is not null)
        {
            CommandRequested?.Invoke(command);
        }
    }

    public void OnStatus(FitnessMachineStatusEvent status, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!IsActivePhase(_phase))
            {
                return;
            }
            if (status.OpCode == FtmsStatusOpCode.ControlPermissionLost)
            {
                FailLocked("The sensor withdrew control (another app may have connected).", now);
            }
            else if (status.SpinDown is { } sd)
            {
                switch (sd)
                {
                    case SpinDownStatus.SpinDownRequested:
                        if (_phase is CalibrationPhase.RequestingControl or CalibrationPhase.Starting or CalibrationPhase.Accelerate)
                        {
                            SetPhase(CalibrationPhase.Accelerate, now);
                        }
                        break;
                    case SpinDownStatus.StopPedaling:
                        SetPhase(CalibrationPhase.Coast, now);
                        break;
                    case SpinDownStatus.Success:
                        _result = "Calibration successful. Power readings are now calibrated for this tyre pressure and roller tension.";
                        SetPhase(CalibrationPhase.Succeeded, now);
                        break;
                    case SpinDownStatus.Error:
                        FailLocked("The sensor reported a calibration error. Pedal above the target speed, then stop pedalling completely and let the wheel coast to a stop.", now);
                        break;
                }
            }
            else
            {
                return;
            }
        }
        Notify();
    }

    public void OnSpeed(double? speedMps, DateTimeOffset now)
    {
        lock (_gate)
        {
            _speedMps = speedMps;
            if (_phase == CalibrationPhase.Accelerate && speedMps >= _highMps)
            {
                _reachedTarget = true;
            }
            if (!IsActivePhase(_phase))
            {
                return;
            }
        }
        Notify();
    }

    /// <summary>Applies phase timeouts. Call about twice a second while active.</summary>
    public void Tick(DateTimeOffset now)
    {
        lock (_gate)
        {
            var elapsed = now - _phaseSince;
            switch (_phase)
            {
                case CalibrationPhase.RequestingControl when elapsed > ControlTimeout:
                    FailLocked("The sensor did not answer the control request.", now);
                    break;
                case CalibrationPhase.Starting when elapsed > ControlTimeout:
                    FailLocked("The sensor did not acknowledge the spin-down request.", now);
                    break;
                case CalibrationPhase.Accelerate when elapsed > AccelerateTimeout:
                    FailLocked("Timed out waiting to reach the target speed.", now);
                    break;
                case CalibrationPhase.Coast when elapsed > CoastTimeout:
                    FailLocked("Timed out waiting for the coast-down result.", now);
                    break;
                default:
                    return;
            }
        }
        Notify();
    }

    public void Cancel(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!IsActivePhase(_phase))
            {
                return;
            }
            _result = "Calibration cancelled.";
            SetPhase(CalibrationPhase.Cancelled, now);
        }
        Notify();
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!IsActivePhase(_phase))
            {
                return;
            }
            FailLocked(reason, now);
        }
        Notify();
    }

    private void FailLocked(string reason, DateTimeOffset now)
    {
        _result = reason;
        SetPhase(CalibrationPhase.Failed, now);
    }

    private void SetPhase(CalibrationPhase phase, DateTimeOffset now)
    {
        _phase = phase;
        _phaseSince = now;
    }

    private void Notify() => Changed?.Invoke(Snapshot);

    private CalibrationSnapshot Build()
    {
        var speedText = _speedMps is { } s ? FormatSpeed(s) : "—";
        var (instruction, detail) = _phase switch
        {
            CalibrationPhase.Idle => ("", ""),
            CalibrationPhase.RequestingControl => ("Contacting the sensor…", ""),
            CalibrationPhase.Starting => ("Starting calibration…", ""),
            CalibrationPhase.Accelerate => _reachedTarget
                ? ($"Keep pedalling above {FormatSpeed(_highMps)}", $"Now {speedText} — waiting for the sensor to say stop")
                : ($"Pedal up to {FormatSpeed(_highMps)}", $"Now {speedText}"),
            CalibrationPhase.Coast => ("STOP PEDALLING — coast until the wheel stops", $"Now {speedText}"),
            CalibrationPhase.Succeeded => ("Calibration complete", _result ?? ""),
            CalibrationPhase.Failed => ("Calibration failed", _result ?? ""),
            CalibrationPhase.Cancelled => ("Calibration cancelled", ""),
            _ => ("", ""),
        };
        return new CalibrationSnapshot(_phase, instruction, detail, _highMps, _lowMps, _speedMps, IsActivePhase(_phase), _result);
    }
}
