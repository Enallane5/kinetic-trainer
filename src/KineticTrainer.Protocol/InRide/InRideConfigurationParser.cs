using System.Buffers.Binary;

namespace KineticTrainer.Protocol.InRide;

/// <summary>Port of <c>inride_process_config_data</c> (inRide.c). The configuration packet is not obfuscated.</summary>
public static class InRideConfigurationParser
{
    public static InRideConfiguration Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != InRideConstants.PacketLength)
        {
            throw new ArgumentException($"Configuration packet must be {InRideConstants.PacketLength} bytes.", nameof(data));
        }

        var currentSpindownTicks = BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]);
        var currentSpindownTime = currentSpindownTicks / InRideConstants.SensorHz;

        return new InRideConfiguration
        {
            RawBytes = data.ToArray(),
            CalibrationReadyTicks = BinaryPrimitives.ReadUInt16LittleEndian(data[0..2]),
            CalibrationStartTicks = BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]),
            CalibrationEndTicks = BinaryPrimitives.ReadUInt16LittleEndian(data[4..6]),
            CalibrationDebounceTicks = BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]),
            CurrentSpindownTicks = currentSpindownTicks,
            CurrentSpindownSeconds = currentSpindownTime,
            UpdateRateDefaultRaw = BinaryPrimitives.ReadUInt16LittleEndian(data[12..14]),
            UpdateRateCalibrationRaw = BinaryPrimitives.ReadUInt16LittleEndian(data[14..16]),
            ProFlywheel = InRidePowerParser.HasProFlywheel(currentSpindownTime),
            UnusedTail = data[16..20].ToArray(),
        };
    }
}
