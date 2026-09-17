namespace KineticTrainer.Recording.Fit;

/// <summary>The FIT CRC-16 (Garmin FIT SDK, "CRC.cs"): nibble-table algorithm, initial value 0.</summary>
public static class FitCrc
{
    private static readonly ushort[] Table =
    [
        0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401,
        0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400,
    ];

    public static ushort Update(ushort crc, byte value)
    {
        // low nibble
        var tmp = Table[crc & 0xF];
        crc = (ushort)((crc >> 4) & 0x0FFF);
        crc = (ushort)(crc ^ tmp ^ Table[value & 0xF]);
        // high nibble
        tmp = Table[crc & 0xF];
        crc = (ushort)((crc >> 4) & 0x0FFF);
        crc = (ushort)(crc ^ tmp ^ Table[(value >> 4) & 0xF]);
        return crc;
    }

    public static ushort Compute(ReadOnlySpan<byte> data, ushort seed = 0)
    {
        var crc = seed;
        foreach (var b in data)
        {
            crc = Update(crc, b);
        }
        return crc;
    }
}
