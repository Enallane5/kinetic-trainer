using System.Buffers.Binary;

namespace KineticTrainer.Protocol.Ftms;

/// <summary>
/// Decoded Fitness Machine Feature characteristic (0x2ACC): two little-endian uint32 bitfields -
/// what the machine measures, and what targets it accepts.
/// </summary>
public sealed record FitnessMachineFeature
{
    public required byte[] RawBytes { get; init; }

    public required FitnessMachineFeatures Features { get; init; }

    public required TargetSettingFeatures TargetSettings { get; init; }

    public bool SupportsPower => Features.HasFlag(FitnessMachineFeatures.PowerMeasurement);

    public bool SupportsCadence => Features.HasFlag(FitnessMachineFeatures.Cadence);

    /// <summary>True when the machine can run the FTMS spin-down (calibration) procedure.</summary>
    public bool SupportsSpinDown => TargetSettings.HasFlag(TargetSettingFeatures.SpinDownControl);

    /// <summary>True when the machine accepts a power target (i.e. it is a controllable smart trainer).</summary>
    public bool SupportsTargetPower => TargetSettings.HasFlag(TargetSettingFeatures.PowerTarget);

    /// <summary>True when the machine accepts resistance control.</summary>
    public bool SupportsTargetResistance => TargetSettings.HasFlag(TargetSettingFeatures.ResistanceTarget);

    public string RawHex => Convert.ToHexStringLower(RawBytes);

    public override string ToString() =>
        $"features=0x{(uint)Features:X8} [{Features}] targets=0x{(uint)TargetSettings:X8} [{TargetSettings}]";

    public static FtmsResult<FitnessMachineFeature> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return FtmsResult<FitnessMachineFeature>.Fail($"Fitness Machine Feature must be at least 8 bytes, got {data.Length}");
        }
        return FtmsResult<FitnessMachineFeature>.Ok(new FitnessMachineFeature
        {
            RawBytes = data.ToArray(),
            Features = (FitnessMachineFeatures)BinaryPrimitives.ReadUInt32LittleEndian(data),
            TargetSettings = (TargetSettingFeatures)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
        });
    }
}
