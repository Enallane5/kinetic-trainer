using System.Buffers.Binary;

namespace KineticTrainer.Protocol.Ftms;

/// <summary>
/// Builds Control Point (0x2AD9) writes and parses the indicated responses.
/// A response is always: 0x80, the op code being answered, a result code, then any response parameter.
/// </summary>
public static class FtmsControlPoint
{
    /// <summary>0x00 - must be accepted before the machine will act on any other write.</summary>
    public static byte[] RequestControl() => [(byte)FtmsOpCode.RequestControl];

    /// <summary>0x01 - return the machine to its default state.</summary>
    public static byte[] Reset() => [(byte)FtmsOpCode.Reset];

    /// <summary>0x07 - start or resume.</summary>
    public static byte[] StartOrResume() => [(byte)FtmsOpCode.StartOrResume];

    /// <summary>0x08 - 0x01 stop, 0x02 pause.</summary>
    public static byte[] StopOrPause(bool pause = false) => [(byte)FtmsOpCode.StopOrPause, pause ? (byte)0x02 : (byte)0x01];

    /// <summary>0x13 0x01 - begin the spin-down (calibration) procedure.</summary>
    public static byte[] StartSpinDown() => [(byte)FtmsOpCode.SpinDownControl, (byte)SpinDownControlParameter.Start];

    /// <summary>0x13 0x02 - cancel/ignore a spin-down the machine offered.</summary>
    public static byte[] IgnoreSpinDown() => [(byte)FtmsOpCode.SpinDownControl, (byte)SpinDownControlParameter.Ignore];

    /// <summary>0x05 - target power in watts (controllable trainers only; a fluid trainer answers OpCodeNotSupported).</summary>
    public static byte[] SetTargetPower(short watts)
    {
        var cmd = new byte[3];
        cmd[0] = (byte)FtmsOpCode.SetTargetPower;
        BinaryPrimitives.WriteInt16LittleEndian(cmd.AsSpan(1), watts);
        return cmd;
    }

    /// <summary>0x12 - wheel circumference in millimetres (0.1 mm resolution on the wire).</summary>
    public static byte[] SetWheelCircumference(double millimetres)
    {
        var cmd = new byte[3];
        cmd[0] = (byte)FtmsOpCode.SetWheelCircumference;
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(1), (ushort)Math.Round(millimetres / 0.1));
        return cmd;
    }

    public static FtmsResult<FtmsControlPointResponse> ParseResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return FtmsResult<FtmsControlPointResponse>.Fail($"Control Point response must be at least 3 bytes, got {data.Length}");
        }
        if (data[0] != (byte)FtmsOpCode.ResponseCode)
        {
            return FtmsResult<FtmsControlPointResponse>.Fail($"Control Point response must start with 0x80, got 0x{data[0]:X2}");
        }

        var request = (FtmsOpCode)data[1];
        var result = (FtmsResultCode)data[2];
        var parameter = data[3..].ToArray();

        double? targetSpeedLowKph = null, targetSpeedHighKph = null;
        // The spin-down response carries the speed window the rider must reach and coast down through.
        if (request == FtmsOpCode.SpinDownControl && result == FtmsResultCode.Success && parameter.Length >= 4)
        {
            targetSpeedLowKph = BinaryPrimitives.ReadUInt16LittleEndian(parameter) * IndoorBikeDataParser.SpeedResolutionKph;
            targetSpeedHighKph = BinaryPrimitives.ReadUInt16LittleEndian(parameter.AsSpan(2)) * IndoorBikeDataParser.SpeedResolutionKph;
        }

        return FtmsResult<FtmsControlPointResponse>.Ok(new FtmsControlPointResponse
        {
            RawBytes = data.ToArray(),
            RequestOpCode = request,
            Result = result,
            Parameter = parameter,
            SpinDownTargetSpeedLowKph = targetSpeedLowKph,
            SpinDownTargetSpeedHighKph = targetSpeedHighKph,
        });
    }
}

public sealed record FtmsControlPointResponse
{
    public required byte[] RawBytes { get; init; }

    public required FtmsOpCode RequestOpCode { get; init; }

    public required FtmsResultCode Result { get; init; }

    public required byte[] Parameter { get; init; }

    public double? SpinDownTargetSpeedLowKph { get; init; }

    public double? SpinDownTargetSpeedHighKph { get; init; }

    public bool IsSuccess => Result == FtmsResultCode.Success;

    public string RawHex => Convert.ToHexStringLower(RawBytes);

    public override string ToString()
    {
        var text = $"{RequestOpCode} -> {Result}";
        if (SpinDownTargetSpeedLowKph is { } low && SpinDownTargetSpeedHighKph is { } high)
        {
            text += $" (coast-down window {low:0.0}-{high:0.0} km/h)";
        }
        else if (Parameter.Length > 0)
        {
            text += $" param={Convert.ToHexStringLower(Parameter)}";
        }
        return text;
    }
}

/// <summary>Decoded Fitness Machine Status notification (0x2ADA).</summary>
public sealed record FitnessMachineStatusEvent
{
    public required byte[] RawBytes { get; init; }

    public required FtmsStatusOpCode OpCode { get; init; }

    public required byte[] Parameter { get; init; }

    /// <summary>Set when <see cref="OpCode"/> is <see cref="FtmsStatusOpCode.SpinDownStatus"/>.</summary>
    public SpinDownStatus? SpinDown { get; init; }

    public string RawHex => Convert.ToHexStringLower(RawBytes);

    public override string ToString() => SpinDown is { } s
        ? $"{OpCode}: {s}"
        : Parameter.Length > 0
            ? $"{OpCode} param={Convert.ToHexStringLower(Parameter)}"
            : OpCode.ToString();

    public static FtmsResult<FitnessMachineStatusEvent> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return FtmsResult<FitnessMachineStatusEvent>.Fail("Fitness Machine Status packet is empty");
        }
        var opCode = (FtmsStatusOpCode)data[0];
        var parameter = data[1..].ToArray();
        SpinDownStatus? spinDown = null;
        if (opCode == FtmsStatusOpCode.SpinDownStatus)
        {
            if (parameter.Length < 1)
            {
                return FtmsResult<FitnessMachineStatusEvent>.Fail("Spin Down Status carries no status byte");
            }
            spinDown = (SpinDownStatus)parameter[0];
        }
        return FtmsResult<FitnessMachineStatusEvent>.Ok(new FitnessMachineStatusEvent
        {
            RawBytes = data.ToArray(),
            OpCode = opCode,
            Parameter = parameter,
            SpinDown = spinDown,
        });
    }
}
