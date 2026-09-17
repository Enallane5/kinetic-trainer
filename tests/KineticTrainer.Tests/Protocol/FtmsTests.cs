using KineticTrainer.Protocol.Ftms;
using Xunit;

namespace KineticTrainer.Tests.Protocol;

/// <summary>
/// FTMS Indoor Bike Data / Feature / Control Point / Status decoding. The "observed" vectors were captured from the
/// physical Kinetic inRide (FW 1.2) on 2026-09-15 with tools/BleProbe.
/// </summary>
public class FtmsTests
{
    [Fact]
    public void Observed_idle_packet_from_real_sensor_decodes_to_speed_cadence_power_present_and_zero()
    {
        // 44 00 | 00 00 | 00 00 | 00 00  -> flags 0x0044: bit0 clear (speed present), bit2 cadence, bit6 power
        var r = IndoorBikeDataParser.Parse(Convert.FromHexString("4400000000000000"));
        Assert.True(r.IsSuccess, r.Error);
        var b = r.Value;
        Assert.Equal((IndoorBikeDataFlags)0x0044, b.Flags);
        Assert.Equal(0.0, b.InstantaneousSpeedKph);
        Assert.Equal(0.0, b.InstantaneousCadenceRpm);
        Assert.Equal((short)0, b.InstantaneousPowerWatts);
        Assert.Null(b.AverageSpeedKph);
        Assert.Null(b.TotalDistanceMeters);
        Assert.Null(b.HeartRateBpm);
        Assert.Equal(8, IndoorBikeDataParser.ExpectedLength(b.Flags));
    }

    [Fact]
    public void Fields_are_little_endian_with_spec_resolutions()
    {
        // speed 32.50 km/h = 3250 = 0x0CB2 ; cadence 88.5 rpm = 177 = 0x00B1 ; power 210 W = 0x00D2
        var r = IndoorBikeDataParser.Parse(Convert.FromHexString("4400" + "b20c" + "b100" + "d200"));
        Assert.True(r.IsSuccess);
        Assert.Equal(32.5, r.Value.InstantaneousSpeedKph!.Value, 9);
        Assert.Equal(88.5, r.Value.InstantaneousCadenceRpm!.Value, 9);
        Assert.Equal((short)210, r.Value.InstantaneousPowerWatts);
    }

    [Fact]
    public void Power_is_signed()
    {
        var r = IndoorBikeDataParser.Parse(Convert.FromHexString("4400" + "0000" + "0000" + "fbff"));
        Assert.Equal((short)-5, r.Value!.InstantaneousPowerWatts);
    }

    [Fact]
    public void More_data_bit_set_means_speed_absent()
    {
        // flags 0x0045: MoreData set (no speed), cadence, power
        var r = IndoorBikeDataParser.Parse(Convert.FromHexString("4500" + "b100" + "d200"));
        Assert.True(r.IsSuccess, r.Error);
        Assert.Null(r.Value.InstantaneousSpeedKph);
        Assert.Equal(88.5, r.Value.InstantaneousCadenceRpm!.Value, 9);
        Assert.Equal((short)210, r.Value.InstantaneousPowerWatts);
    }

    [Fact]
    public void Every_field_is_consumed_in_flag_order()
    {
        // All bits 1..12 set plus speed present (bit0 clear): 0x1FFE
        var hex = "fe1f" +
                  "e803" +      // speed 10.00 km/h
                  "d007" +      // avg speed 20.00
                  "b400" +      // cadence 90.0
                  "a000" +      // avg cadence 80.0
                  "e80300" +    // distance 1000 m (uint24)
                  "0500" +      // resistance 5
                  "c800" +      // power 200
                  "b400" +      // avg power 180
                  "3200" + "9001" + "07" + // energy: total 50 kcal, per hour 400, per minute 7
                  "8c" +        // heart rate 140
                  "5a" +        // MET 9.0
                  "3c00" +      // elapsed 60 s
                  "2c01";       // remaining 300 s
        var r = IndoorBikeDataParser.Parse(Convert.FromHexString(hex));
        Assert.True(r.IsSuccess, r.Error);
        var b = r.Value;
        Assert.Equal(10.0, b.InstantaneousSpeedKph!.Value, 9);
        Assert.Equal(20.0, b.AverageSpeedKph!.Value, 9);
        Assert.Equal(90.0, b.InstantaneousCadenceRpm!.Value, 9);
        Assert.Equal(80.0, b.AverageCadenceRpm!.Value, 9);
        Assert.Equal(1000u, b.TotalDistanceMeters);
        Assert.Equal((short)5, b.ResistanceLevel);
        Assert.Equal((short)200, b.InstantaneousPowerWatts);
        Assert.Equal((short)180, b.AveragePowerWatts);
        Assert.Equal((ushort)50, b.TotalEnergyKcal);
        Assert.Equal((ushort)400, b.EnergyPerHourKcal);
        Assert.Equal((byte)7, b.EnergyPerMinuteKcal);
        Assert.Equal((byte)140, b.HeartRateBpm);
        Assert.Equal(9.0, b.MetabolicEquivalent!.Value, 9);
        Assert.Equal((ushort)60, b.ElapsedTimeSeconds);
        Assert.Equal((ushort)300, b.RemainingTimeSeconds);
        Assert.Equal(hex.Length / 2, IndoorBikeDataParser.ExpectedLength(b.Flags));
    }

    [Theory]
    [InlineData("")]
    [InlineData("44")]
    [InlineData("440000")]
    [InlineData("44000000000000")]
    [InlineData("fe1fe803")]
    public void Truncated_packets_are_rejected_with_reason(string hex)
    {
        var r = IndoorBikeDataParser.Parse(Convert.FromHexString(hex));
        Assert.False(r.IsSuccess);
        Assert.Contains(hex.Length < 4 ? "too short" : "require", r.Error);
    }

    [Fact]
    public void Extra_trailing_bytes_are_tolerated()
    {
        var r = IndoorBikeDataParser.Parse(Convert.FromHexString("4400000000000000" + "aabb"));
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public void Build_round_trips_through_parse()
    {
        var packet = IndoorBikeDataParser.Build(speedKph: 31.27, cadenceRpm: 91.5, powerWatts: 245, distanceMeters: 123456, elapsedSeconds: 900, heartRate: 150);
        var r = IndoorBikeDataParser.Parse(packet);
        Assert.True(r.IsSuccess, r.Error);
        Assert.Equal(31.27, r.Value.InstantaneousSpeedKph!.Value, 9);
        Assert.Equal(91.5, r.Value.InstantaneousCadenceRpm!.Value, 9);
        Assert.Equal((short)245, r.Value.InstantaneousPowerWatts);
        Assert.Equal(123456u, r.Value.TotalDistanceMeters);
        Assert.Equal((ushort)900, r.Value.ElapsedTimeSeconds);
        Assert.Equal((byte)150, r.Value.HeartRateBpm);
    }

    [Fact]
    public void Observed_feature_value_decodes_to_cadence_power_and_spin_down()
    {
        var r = FitnessMachineFeature.Parse(Convert.FromHexString("0240000000800000"));
        Assert.True(r.IsSuccess);
        var f = r.Value;
        Assert.Equal(FitnessMachineFeatures.Cadence | FitnessMachineFeatures.PowerMeasurement, f.Features);
        Assert.Equal(TargetSettingFeatures.SpinDownControl, f.TargetSettings);
        Assert.True(f.SupportsPower);
        Assert.True(f.SupportsCadence);
        Assert.True(f.SupportsSpinDown);
        Assert.False(f.SupportsTargetPower);
        Assert.False(f.SupportsTargetResistance);
        Assert.False(FitnessMachineFeature.Parse(new byte[7]).IsSuccess);
    }

    [Fact]
    public void Control_point_commands_have_spec_opcodes()
    {
        Assert.Equal(new byte[] { 0x00 }, FtmsControlPoint.RequestControl());
        Assert.Equal(new byte[] { 0x01 }, FtmsControlPoint.Reset());
        Assert.Equal(new byte[] { 0x07 }, FtmsControlPoint.StartOrResume());
        Assert.Equal(new byte[] { 0x08, 0x01 }, FtmsControlPoint.StopOrPause());
        Assert.Equal(new byte[] { 0x08, 0x02 }, FtmsControlPoint.StopOrPause(pause: true));
        Assert.Equal(new byte[] { 0x13, 0x01 }, FtmsControlPoint.StartSpinDown());
        Assert.Equal(new byte[] { 0x13, 0x02 }, FtmsControlPoint.IgnoreSpinDown());
        Assert.Equal(new byte[] { 0x05, 0xC8, 0x00 }, FtmsControlPoint.SetTargetPower(200));
        Assert.Equal(new byte[] { 0x12, 0x08, 0x52 }, FtmsControlPoint.SetWheelCircumference(2100)); // 2100 mm = 21000 x 0.1 mm = 0x5208 LE
    }

    [Fact]
    public void Control_point_responses_parse_including_spin_down_window()
    {
        var ok = FtmsControlPoint.ParseResponse([0x80, 0x00, 0x01]);
        Assert.True(ok.IsSuccess);
        Assert.Equal(FtmsOpCode.RequestControl, ok.Value.RequestOpCode);
        Assert.Equal(FtmsResultCode.Success, ok.Value.Result);
        Assert.True(ok.Value.IsSuccess);

        var denied = FtmsControlPoint.ParseResponse([0x80, 0x13, 0x05]);
        Assert.Equal(FtmsResultCode.ControlNotPermitted, denied.Value!.Result);

        // spin down accepted: low 20.00 km/h (2000 = 0x07D0), high 40.00 km/h (4000 = 0x0FA0)
        var spin = FtmsControlPoint.ParseResponse([0x80, 0x13, 0x01, 0xD0, 0x07, 0xA0, 0x0F]);
        Assert.True(spin.IsSuccess);
        Assert.Equal(20.0, spin.Value.SpinDownTargetSpeedLowKph!.Value, 9);
        Assert.Equal(40.0, spin.Value.SpinDownTargetSpeedHighKph!.Value, 9);
        Assert.Contains("20.0-40.0", spin.Value.ToString());

        Assert.False(FtmsControlPoint.ParseResponse([0x80, 0x00]).IsSuccess);
        Assert.False(FtmsControlPoint.ParseResponse([0x00, 0x00, 0x01]).IsSuccess);
    }

    [Fact]
    public void Machine_status_spin_down_sequence_parses()
    {
        foreach (var (raw, expected) in new[] { ((byte)0x01, SpinDownStatus.SpinDownRequested), ((byte)0x02, SpinDownStatus.Success),
                     ((byte)0x03, SpinDownStatus.Error), ((byte)0x04, SpinDownStatus.StopPedaling) })
        {
            var r = FitnessMachineStatusEvent.Parse([0x14, raw]);
            Assert.True(r.IsSuccess);
            Assert.Equal(FtmsStatusOpCode.SpinDownStatus, r.Value.OpCode);
            Assert.Equal(expected, r.Value.SpinDown);
        }
        Assert.Equal(FtmsStatusOpCode.ControlPermissionLost, FitnessMachineStatusEvent.Parse([0xFF]).Value!.OpCode);
        Assert.False(FitnessMachineStatusEvent.Parse([]).IsSuccess);
        Assert.False(FitnessMachineStatusEvent.Parse([0x14]).IsSuccess);
    }

    [Fact]
    public void Uuids_are_bluetooth_sig_base()
    {
        Assert.Equal("00001826-0000-1000-8000-00805f9b34fb", FtmsUuids.FitnessMachineService.ToString());
        Assert.Equal("00002ad2-0000-1000-8000-00805f9b34fb", FtmsUuids.IndoorBikeData.ToString());
        Assert.Equal("00002ad9-0000-1000-8000-00805f9b34fb", FtmsUuids.FitnessMachineControlPoint.ToString());
        Assert.Equal("00002acc-0000-1000-8000-00805f9b34fb", FtmsUuids.FitnessMachineFeature.ToString());
        Assert.Equal("00002ada-0000-1000-8000-00805f9b34fb", FtmsUuids.FitnessMachineStatus.ToString());
    }
}
