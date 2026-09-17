using System.Buffers.Binary;

namespace KineticTrainer.Protocol.Ftms;

/// <summary>
/// One decoded Indoor Bike Data notification (0x2AD2). Only the fields the flags announce are populated;
/// everything else is null. Units are as transmitted, converted to their natural form (km/h, rpm, W, m, s).
/// </summary>
public sealed record IndoorBikeData
{
    public required byte[] RawBytes { get; init; }

    public required IndoorBikeDataFlags Flags { get; init; }

    /// <summary>km/h. Present when the "More Data" bit is CLEAR.</summary>
    public double? InstantaneousSpeedKph { get; init; }

    public double? AverageSpeedKph { get; init; }

    /// <summary>Crank RPM.</summary>
    public double? InstantaneousCadenceRpm { get; init; }

    public double? AverageCadenceRpm { get; init; }

    /// <summary>Metres.</summary>
    public uint? TotalDistanceMeters { get; init; }

    public short? ResistanceLevel { get; init; }

    /// <summary>Watts (signed: braking/coasting trainers may report negative).</summary>
    public short? InstantaneousPowerWatts { get; init; }

    public short? AveragePowerWatts { get; init; }

    public ushort? TotalEnergyKcal { get; init; }

    public ushort? EnergyPerHourKcal { get; init; }

    public byte? EnergyPerMinuteKcal { get; init; }

    public byte? HeartRateBpm { get; init; }

    public double? MetabolicEquivalent { get; init; }

    public ushort? ElapsedTimeSeconds { get; init; }

    public ushort? RemainingTimeSeconds { get; init; }

    public string RawHex => Convert.ToHexStringLower(RawBytes);

    /// <summary>Compact one-line description for the debug log.</summary>
    public override string ToString()
    {
        var parts = new List<string> { $"flags=0x{(ushort)Flags:X4}" };
        if (InstantaneousSpeedKph is { } s) parts.Add($"speed={s:0.00}kph");
        if (AverageSpeedKph is { } avs) parts.Add($"avgSpeed={avs:0.00}kph");
        if (InstantaneousCadenceRpm is { } c) parts.Add($"cadence={c:0.0}rpm");
        if (AverageCadenceRpm is { } ac) parts.Add($"avgCadence={ac:0.0}rpm");
        if (TotalDistanceMeters is { } d) parts.Add($"distance={d}m");
        if (ResistanceLevel is { } r) parts.Add($"resistance={r}");
        if (InstantaneousPowerWatts is { } p) parts.Add($"power={p}W");
        if (AveragePowerWatts is { } ap) parts.Add($"avgPower={ap}W");
        if (TotalEnergyKcal is { } e) parts.Add($"energy={e}kcal");
        if (HeartRateBpm is { } hr) parts.Add($"hr={hr}bpm");
        if (MetabolicEquivalent is { } met) parts.Add($"met={met:0.0}");
        if (ElapsedTimeSeconds is { } et) parts.Add($"elapsed={et}s");
        if (RemainingTimeSeconds is { } rt) parts.Add($"remaining={rt}s");
        return string.Join(' ', parts);
    }
}

/// <summary>
/// Parser for Indoor Bike Data (0x2AD2). Field order and units are fixed by the FTMS specification;
/// the flags word says which fields are present, and every present field is consumed in bit order.
///
/// Wire order after the 2-byte flags:
///   bit 0 clear -> Instantaneous Speed   uint16, 0.01 km/h
///   bit 1       -> Average Speed         uint16, 0.01 km/h
///   bit 2       -> Instantaneous Cadence uint16, 0.5 rpm
///   bit 3       -> Average Cadence       uint16, 0.5 rpm
///   bit 4       -> Total Distance        uint24, 1 m
///   bit 5       -> Resistance Level      sint16, unitless
///   bit 6       -> Instantaneous Power   sint16, 1 W
///   bit 7       -> Average Power         sint16, 1 W
///   bit 8       -> Total Energy uint16 kcal + Energy Per Hour uint16 kcal + Energy Per Minute uint8 kcal
///   bit 9       -> Heart Rate            uint8, bpm
///   bit 10      -> Metabolic Equivalent  uint8, 0.1
///   bit 11      -> Elapsed Time          uint16, 1 s
///   bit 12      -> Remaining Time        uint16, 1 s
/// </summary>
public static class IndoorBikeDataParser
{
    public const double SpeedResolutionKph = 0.01;
    public const double CadenceResolutionRpm = 0.5;
    public const double MetabolicEquivalentResolution = 0.1;

    /// <summary>Bytes required by a flags word, including the 2 flag bytes themselves.</summary>
    public static int ExpectedLength(IndoorBikeDataFlags flags)
    {
        var length = 2;
        if (!flags.HasFlag(IndoorBikeDataFlags.MoreData)) length += 2;   // instantaneous speed (inverted bit)
        if (flags.HasFlag(IndoorBikeDataFlags.AverageSpeed)) length += 2;
        if (flags.HasFlag(IndoorBikeDataFlags.InstantaneousCadence)) length += 2;
        if (flags.HasFlag(IndoorBikeDataFlags.AverageCadence)) length += 2;
        if (flags.HasFlag(IndoorBikeDataFlags.TotalDistance)) length += 3;
        if (flags.HasFlag(IndoorBikeDataFlags.ResistanceLevel)) length += 2;
        if (flags.HasFlag(IndoorBikeDataFlags.InstantaneousPower)) length += 2;
        if (flags.HasFlag(IndoorBikeDataFlags.AveragePower)) length += 2;
        if (flags.HasFlag(IndoorBikeDataFlags.ExpendedEnergy)) length += 5;
        if (flags.HasFlag(IndoorBikeDataFlags.HeartRate)) length += 1;
        if (flags.HasFlag(IndoorBikeDataFlags.MetabolicEquivalent)) length += 1;
        if (flags.HasFlag(IndoorBikeDataFlags.ElapsedTime)) length += 2;
        if (flags.HasFlag(IndoorBikeDataFlags.RemainingTime)) length += 2;
        return length;
    }

    /// <summary>Parses a notification. Returns a failure result rather than throwing for malformed data.</summary>
    public static FtmsResult<IndoorBikeData> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return FtmsResult<IndoorBikeData>.Fail($"Indoor Bike Data packet too short: {data.Length} bytes, need at least 2 for the flags");
        }

        var flags = (IndoorBikeDataFlags)BinaryPrimitives.ReadUInt16LittleEndian(data);
        var expected = ExpectedLength(flags);
        if (data.Length < expected)
        {
            return FtmsResult<IndoorBikeData>.Fail(
                $"Indoor Bike Data packet is {data.Length} bytes but flags 0x{(ushort)flags:X4} require {expected}");
        }

        var offset = 2;
        double? speed = null, avgSpeed = null, cadence = null, avgCadence = null, met = null;
        uint? distance = null;
        short? resistance = null, power = null, avgPower = null;
        ushort? totalEnergy = null, energyPerHour = null, elapsed = null, remaining = null;
        byte? energyPerMinute = null, heartRate = null;

        // Bit 0 is "More Data": CLEAR means the speed field IS present.
        if (!flags.HasFlag(IndoorBikeDataFlags.MoreData))
        {
            speed = ReadU16(data, ref offset) * SpeedResolutionKph;
        }
        if (flags.HasFlag(IndoorBikeDataFlags.AverageSpeed))
        {
            avgSpeed = ReadU16(data, ref offset) * SpeedResolutionKph;
        }
        if (flags.HasFlag(IndoorBikeDataFlags.InstantaneousCadence))
        {
            cadence = ReadU16(data, ref offset) * CadenceResolutionRpm;
        }
        if (flags.HasFlag(IndoorBikeDataFlags.AverageCadence))
        {
            avgCadence = ReadU16(data, ref offset) * CadenceResolutionRpm;
        }
        if (flags.HasFlag(IndoorBikeDataFlags.TotalDistance))
        {
            distance = (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16));
            offset += 3;
        }
        if (flags.HasFlag(IndoorBikeDataFlags.ResistanceLevel))
        {
            resistance = ReadS16(data, ref offset);
        }
        if (flags.HasFlag(IndoorBikeDataFlags.InstantaneousPower))
        {
            power = ReadS16(data, ref offset);
        }
        if (flags.HasFlag(IndoorBikeDataFlags.AveragePower))
        {
            avgPower = ReadS16(data, ref offset);
        }
        if (flags.HasFlag(IndoorBikeDataFlags.ExpendedEnergy))
        {
            totalEnergy = ReadU16(data, ref offset);
            energyPerHour = ReadU16(data, ref offset);
            energyPerMinute = data[offset++];
        }
        if (flags.HasFlag(IndoorBikeDataFlags.HeartRate))
        {
            heartRate = data[offset++];
        }
        if (flags.HasFlag(IndoorBikeDataFlags.MetabolicEquivalent))
        {
            met = data[offset++] * MetabolicEquivalentResolution;
        }
        if (flags.HasFlag(IndoorBikeDataFlags.ElapsedTime))
        {
            elapsed = ReadU16(data, ref offset);
        }
        if (flags.HasFlag(IndoorBikeDataFlags.RemainingTime))
        {
            remaining = ReadU16(data, ref offset);
        }

        return FtmsResult<IndoorBikeData>.Ok(new IndoorBikeData
        {
            RawBytes = data.ToArray(),
            Flags = flags,
            InstantaneousSpeedKph = speed,
            AverageSpeedKph = avgSpeed,
            InstantaneousCadenceRpm = cadence,
            AverageCadenceRpm = avgCadence,
            TotalDistanceMeters = distance,
            ResistanceLevel = resistance,
            InstantaneousPowerWatts = power,
            AveragePowerWatts = avgPower,
            TotalEnergyKcal = totalEnergy,
            EnergyPerHourKcal = energyPerHour,
            EnergyPerMinuteKcal = energyPerMinute,
            HeartRateBpm = heartRate,
            MetabolicEquivalent = met,
            ElapsedTimeSeconds = elapsed,
            RemainingTimeSeconds = remaining,
        });
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, ref int offset)
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        return v;
    }

    private static short ReadS16(ReadOnlySpan<byte> data, ref int offset)
    {
        var v = BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
        offset += 2;
        return v;
    }

    /// <summary>Builds a packet with the given fields - used by tests and the simulated FTMS sensor.</summary>
    public static byte[] Build(double? speedKph = null, double? cadenceRpm = null, short? powerWatts = null,
        uint? distanceMeters = null, ushort? elapsedSeconds = null, byte? heartRate = null)
    {
        var flags = default(IndoorBikeDataFlags);
        if (speedKph is null) flags |= IndoorBikeDataFlags.MoreData; // set = speed absent
        if (cadenceRpm is not null) flags |= IndoorBikeDataFlags.InstantaneousCadence;
        if (distanceMeters is not null) flags |= IndoorBikeDataFlags.TotalDistance;
        if (powerWatts is not null) flags |= IndoorBikeDataFlags.InstantaneousPower;
        if (heartRate is not null) flags |= IndoorBikeDataFlags.HeartRate;
        if (elapsedSeconds is not null) flags |= IndoorBikeDataFlags.ElapsedTime;

        var buffer = new byte[ExpectedLength(flags)];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)flags);
        var offset = 2;
        if (speedKph is { } s)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)Math.Round(s / SpeedResolutionKph));
            offset += 2;
        }
        if (cadenceRpm is { } c)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)Math.Round(c / CadenceResolutionRpm));
            offset += 2;
        }
        if (distanceMeters is { } d)
        {
            buffer[offset] = (byte)d;
            buffer[offset + 1] = (byte)(d >> 8);
            buffer[offset + 2] = (byte)(d >> 16);
            offset += 3;
        }
        if (powerWatts is { } p)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset), p);
            offset += 2;
        }
        if (heartRate is { } hr)
        {
            buffer[offset++] = hr;
        }
        if (elapsedSeconds is { } et)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), et);
        }
        return buffer;
    }
}
