namespace KineticTrainer.Protocol.InRide;

/// <summary>
/// Undoes the byte scrambling the inRide 2 applies to Power notifications.
/// Straight port of the first half of <c>inride_process_power_data</c> (inRide.c).
///
/// Layout of the 20-byte notification as received:
///   byte 0      header. Bits 7-6 = rotation index (0-3) selecting the permutation table; bits 5-4 = sensor state;
///               bits 3-0 = last command result. Byte 0 is never XORed or moved.
///   bytes 1-19  payload, XORed then permuted.
///
/// De-obfuscation:
///   1. rotate  = (data[0] &amp; 0xC0) >> 6
///   2. xorIdx1 = (rotate + 1) % 4 ; xorIdx2 = (xorIdx1 + 1) % 4
///   3. for i in 1..19:  deob[i] = data[i] XOR (Indices[xorIdx1][i-1] + Indices[xorIdx2][i-1])
///   4. plain[0] = deob[0];  for i in 0..18:  plain[i+1] = deob[Indices[rotate][i]]
/// The result ("powerBytes" in Kinetic's code) is the plain little-endian layout parsed by <see cref="InRidePowerParser"/>.
/// </summary>
public static class InRideDeobfuscator
{
    /// <summary>Permutation tables, copied verbatim from inRide.c (<c>static uint8_t indices[4][19]</c>).</summary>
    internal static readonly byte[][] Indices =
    [
        [14, 15, 12, 16, 11, 5, 17, 3, 2, 1, 19, 13, 6, 4, 8, 9, 10, 18, 7],
        [12, 14, 8, 11, 16, 4, 7, 13, 18, 1, 3, 19, 6, 15, 9, 5, 10, 17, 2],
        [11, 5, 1, 9, 4, 18, 7, 15, 6, 2, 10, 12, 16, 3, 14, 13, 19, 17, 8],
        [13, 5, 18, 1, 3, 12, 15, 10, 14, 19, 16, 8, 6, 11, 2, 9, 4, 17, 7],
    ];

    /// <summary>Returns the de-obfuscated 20-byte packet. <paramref name="data"/> must be exactly 20 bytes.</summary>
    public static byte[] Deobfuscate(ReadOnlySpan<byte> data)
    {
        if (data.Length != InRideConstants.PacketLength)
        {
            throw new ArgumentException($"Power packet must be {InRideConstants.PacketLength} bytes.", nameof(data));
        }

        Span<byte> deob = stackalloc byte[InRideConstants.PacketLength];
        data.CopyTo(deob);

        var posRotate = (data[0] & 0xC0) >> 6;
        var xorIdx1 = (posRotate + 1) % 4;
        var xorIdx2 = (xorIdx1 + 1) % 4;

        for (var i = 1; i < InRideConstants.PacketLength; i++)
        {
            deob[i] = (byte)(deob[i] ^ (Indices[xorIdx1][i - 1] + Indices[xorIdx2][i - 1]));
        }

        var powerBytes = new byte[InRideConstants.PacketLength];
        powerBytes[0] = deob[0];
        for (var i = 0; i < InRideConstants.PacketLength - 1; i++)
        {
            powerBytes[i + 1] = deob[Indices[posRotate][i]];
        }
        return powerBytes;
    }

    /// <summary>Exact inverse of <see cref="Deobfuscate"/>; used by tests and the simulated sensor.</summary>
    public static byte[] Obfuscate(ReadOnlySpan<byte> plain)
    {
        if (plain.Length != InRideConstants.PacketLength)
        {
            throw new ArgumentException($"Power packet must be {InRideConstants.PacketLength} bytes.", nameof(plain));
        }

        var rotate = (plain[0] & 0xC0) >> 6;
        var xorIdx1 = (rotate + 1) % 4;
        var xorIdx2 = (xorIdx1 + 1) % 4;

        Span<byte> deob = stackalloc byte[InRideConstants.PacketLength];
        deob[0] = plain[0];
        for (var j = 0; j < InRideConstants.PacketLength - 1; j++)
        {
            deob[Indices[rotate][j]] = plain[j + 1];
        }

        var data = new byte[InRideConstants.PacketLength];
        data[0] = deob[0];
        for (var i = 1; i < InRideConstants.PacketLength; i++)
        {
            data[i] = (byte)(deob[i] ^ (Indices[xorIdx1][i - 1] + Indices[xorIdx2][i - 1]));
        }
        return data;
    }
}
