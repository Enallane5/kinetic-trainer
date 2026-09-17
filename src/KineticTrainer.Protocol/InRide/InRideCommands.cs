using System.Buffers.Binary;
using System.Text;

namespace KineticTrainer.Protocol.InRide;

/// <summary>
/// Builds Control Point payloads (write with response). Port of <c>command_key</c> and the
/// <c>inride_create_*_command_data</c> functions (inRide.c) plus <c>setPeripheralNameCommandData</c> (KineticInRide.m).
///
/// Every command starts with a 2-byte little-endian "command key" derived from the Device Information System ID
/// (6 bytes): key = systemId[systemId[3] % 6] | systemId[systemId[5] % 6] &lt;&lt; 8, followed by the opcode byte.
/// The C structs are <c>__attribute__((packed))</c>, so the wire format is exactly the field bytes, little-endian.
/// </summary>
public static class InRideCommands
{
    /// <summary>inRide.c <c>command_key</c>.</summary>
    public static ushort CommandKey(ReadOnlySpan<byte> systemId)
    {
        RequireSystemId(systemId);
        var sysidx1 = systemId[3] % 6;
        var sysidx2 = systemId[5] % 6;
        return (ushort)(systemId[sysidx1] | (systemId[sysidx2] << 8));
    }

    /// <summary>
    /// Opcode 0x01, 15 bytes: key(2) + 0x01 + calReady(602) + calStart(655) + calEnd(950) + calDebounce(327) +
    /// updateRateDefault(rate) + updateRateFast(0x08 = 4 Hz), all uint16 little-endian. Sets the normal update rate;
    /// the calibration parameters are always rewritten with Kinetic's constants.
    /// </summary>
    public static byte[] ConfigureSensor(ReadOnlySpan<byte> systemId, InRideUpdateRate updateRate)
    {
        var key = CommandKey(systemId);
        var cmd = new byte[15];
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(0, 2), key);
        cmd[2] = (byte)InRideCommand.SetSpindownParams;
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(3, 2), InRideConstants.CalibrationReadyTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(5, 2), InRideConstants.CalibrationStartTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(7, 2), InRideConstants.CalibrationEndTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(9, 2), InRideConstants.CalibrationDebounceTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(11, 2), (ushort)updateRate);
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(13, 2), (ushort)InRideUpdateRate.Millis250);
        return cmd;
    }

    /// <summary>Opcode 0x03, 3 bytes.</summary>
    public static byte[] StartCalibration(ReadOnlySpan<byte> systemId) => KeyAndOpcode(systemId, InRideCommand.StartCalibration);

    /// <summary>Opcode 0x04, 3 bytes.</summary>
    public static byte[] StopCalibration(ReadOnlySpan<byte> systemId) => KeyAndOpcode(systemId, InRideCommand.StopCalibration);

    /// <summary>Opcode 0x05, 7 bytes: key + 0x05 + uint32 little-endian ticks = (uint32)(seconds * 32768).</summary>
    public static byte[] SetSpindownTime(ReadOnlySpan<byte> systemId, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds * InRideConstants.SensorHz > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), "Spindown time must be a non-negative number of seconds.");
        }
        var key = CommandKey(systemId);
        var cmd = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(0, 2), key);
        cmd[2] = (byte)InRideCommand.SetSpindownTime;
        BinaryPrimitives.WriteUInt32LittleEndian(cmd.AsSpan(3, 4), (uint)(seconds * InRideConstants.SensorHz));
        return cmd;
    }

    /// <summary>Opcode 0x02: key + 0x02 + UTF-8 name of 3 to 8 characters (KineticInRide.m).</summary>
    public static byte[] SetPeripheralName(ReadOnlySpan<byte> systemId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length < 3 || name.Length > 8)
        {
            throw new ArgumentException("Sensor name must be between 3 and 8 characters.", nameof(name));
        }
        var key = CommandKey(systemId);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var cmd = new byte[3 + nameBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(0, 2), key);
        cmd[2] = (byte)InRideCommand.SetName;
        nameBytes.CopyTo(cmd, 3);
        return cmd;
    }

    private static byte[] KeyAndOpcode(ReadOnlySpan<byte> systemId, InRideCommand opcode)
    {
        var key = CommandKey(systemId);
        var cmd = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(0, 2), key);
        cmd[2] = (byte)opcode;
        return cmd;
    }

    private static void RequireSystemId(ReadOnlySpan<byte> systemId)
    {
        if (systemId.Length != InRideConstants.SystemIdLength)
        {
            throw new ArgumentException($"System ID must be {InRideConstants.SystemIdLength} bytes.", nameof(systemId));
        }
    }
}
