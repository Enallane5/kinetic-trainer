using System.Buffers.Binary;
using KineticTrainer.Protocol.InRide;
using Xunit;

namespace KineticTrainer.Tests.Protocol;

public class InRideParserTests
{
    /// <summary>Builds the plain (de-obfuscated) layout so tests can state intent in engineering values.</summary>
    internal static byte[] Plain(byte rotation = 0, byte state = 0, byte command = 0, uint interval = 8192, uint ticks = 0,
        byte revs = 0, uint ticksPrev = 0, byte revsPrev = 0, ushort cadenceRaw = 0, uint spindownTicks = 0)
    {
        var b = new byte[20];
        b[0] = (byte)(((rotation & 3) << 6) | (state & 0x30) | (command & 0x0F));
        b[1] = (byte)interval; b[2] = (byte)(interval >> 8); b[3] = (byte)(interval >> 16);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), ticks);
        b[8] = revs;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), ticksPrev);
        b[13] = revsPrev;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), cadenceRaw);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), spindownTicks);
        return b;
    }

    internal static byte[] Wire(byte[] plain) => InRideDeobfuscator.Obfuscate(plain);

    /// <summary>Ticks for a given roller speed with the given revolutions per packet.</summary>
    internal static uint TicksFor(double kph, byte revs) => (uint)Math.Round(InRideConstants.SpeedKphTicksConstant * revs / kph);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Deobfuscation_round_trips_every_rotation(byte rotation)
    {
        var rng = new Random(rotation);
        for (var n = 0; n < 200; n++)
        {
            var plain = new byte[20];
            rng.NextBytes(plain);
            plain[0] = (byte)((plain[0] & 0x3F) | (rotation << 6));
            var wire = InRideDeobfuscator.Obfuscate(plain);
            Assert.Equal(plain, InRideDeobfuscator.Deobfuscate(wire));
            Assert.Equal(plain[0], wire[0]); // header byte is never scrambled
        }
    }

    [Fact]
    public void Header_byte_is_split_into_rotation_state_and_command_result()
    {
        var wire = Wire(Plain(rotation: 2, state: 0x20, command: 0x0A));
        var p = InRidePowerParser.Parse(wire);
        Assert.Equal(2, p.Rotation);
        Assert.Equal(InRideSensorState.SpindownReady, p.State);
        Assert.Equal(InRideCommandResult.CalibrationResult, p.CommandResult);
    }

    [Fact]
    public void Undefined_command_result_bits_are_preserved_not_mapped_to_none()
    {
        var p = InRidePowerParser.Parse(Wire(Plain(command: 0x05)));
        Assert.Equal(0x05, p.CommandResultBits);
        Assert.Equal((InRideCommandResult)0x05, p.CommandResult);
        Assert.False(p.CommandResult.IsDefined());
        Assert.Equal("Undefined(0x5)", p.CommandResult.Describe());
    }

    [Fact]
    public void Speed_and_roller_rpm_follow_kinetic_formulas()
    {
        // 1 revolution per 655 ticks is Kinetic's "calibration start" speed.
        var p = InRidePowerParser.Parse(Wire(Plain(ticks: 655, revs: 1)));
        Assert.Equal(20012.256849 / 655, p.SpeedKph, 12);
        Assert.Equal(1.0 / (655 / 32768.0) * 60, p.RollerRpm, 9);
        // Roller circumference implied by the speed constant is Kinetic's 169.646 mm.
        var metresPerRev = p.SpeedKph / 3.6 / (p.RollerRpm / 60);
        Assert.Equal(InRideConstants.RollerCircumferenceKm * 1000, metresPerRev, 9);
    }

    [Fact]
    public void Zero_ticks_or_zero_revolutions_give_zero_speed()
    {
        Assert.Equal(0, InRidePowerParser.Parse(Wire(Plain(ticks: 0, revs: 5))).SpeedKph);
        Assert.Equal(0, InRidePowerParser.Parse(Wire(Plain(ticks: 5000, revs: 0))).SpeedKph);
        Assert.Equal(0, InRidePowerParser.Parse(Wire(Plain(ticks: 5000, revs: 0))).RollerRpm);
    }

    [Fact]
    public void Cadence_calibration_is_linear_with_zero_passthrough()
    {
        Assert.Equal(0, InRidePowerParser.Parse(Wire(Plain(cadenceRaw: 0))).CadenceCalibratedRpm);
        Assert.Equal(0.8652 * 90 + 5.2617, InRidePowerParser.Parse(Wire(Plain(cadenceRaw: 90))).CadenceCalibratedRpm, 12);
    }

    [Theory]
    [InlineData(1.0, InRideCalibrationResult.TooFast, 1.75, false)]
    [InlineData(1.5, InRideCalibrationResult.Success, 1.5, false)]
    [InlineData(1.8, InRideCalibrationResult.Success, 1.8, false)]
    [InlineData(2.0, InRideCalibrationResult.Success, 2.0, false)]
    [InlineData(3.0, InRideCalibrationResult.Middle, 1.75, false)]
    [InlineData(4.7, InRideCalibrationResult.Success, 4.7, true)]
    [InlineData(6.5, InRideCalibrationResult.Success, 6.5, true)]
    [InlineData(7.0, InRideCalibrationResult.TooSlow, 1.75, false)]
    [InlineData(0.0, InRideCalibrationResult.TooFast, 1.75, false)]
    public void Spindown_windows_select_result_flywheel_and_applied_time(double seconds, InRideCalibrationResult expected,
        double appliedSpindown, bool pro)
    {
        // The sensor reports whole ticks, so the decoded time is ticks/32768 (e.g. 4.7 s -> 154010 ticks -> 4.70001 s).
        var ticks = (uint)Math.Round(seconds * 32768);
        var p = InRidePowerParser.Parse(Wire(Plain(spindownTicks: ticks)));
        Assert.Equal(expected, p.CalibrationResult);
        var expectedApplied = appliedSpindown == 1.75 ? 1.75 : ticks / 32768.0;
        Assert.Equal(expectedApplied, p.SpindownTimeSeconds, 12);
        Assert.Equal(pro, p.ProFlywheel);
    }

    [Fact]
    public void Roller_resistance_is_normalised_spindown()
    {
        Assert.Equal(0.5, InRidePowerParser.Parse(Wire(Plain(spindownTicks: (uint)(1.75 * 32768)))).RollerResistance, 9);
        Assert.Equal(1.0, InRidePowerParser.Parse(Wire(Plain(spindownTicks: (uint)(1.5 * 32768)))).RollerResistance, 9);
        Assert.Equal(0.0, InRidePowerParser.Parse(Wire(Plain(spindownTicks: (uint)(2.0 * 32768)))).RollerResistance, 9);
        Assert.Equal(0.0, InRidePowerParser.Parse(Wire(Plain(spindownTicks: (uint)(6.5 * 32768)))).RollerResistance, 9);
    }

    [Fact]
    public void Power_model_matches_power_for_speed()
    {
        // 30 km/h, default 1.75 s spindown, normal flywheel.
        var mph = 30.0 * 0.621371;
        var raw = 5.244820 * mph + 0.019168 * mph * mph * mph;
        var drag = 4.55 * 1750 * raw * 0.00001 + (-0.1425 * 1750) + 236.20;
        Assert.Equal((int)(raw + drag), InRidePowerParser.PowerForSpeed(30.0, 1.75));

        // Pro flywheel constants.
        var dragPro = 2.62 * 5500 * raw * 0.00001 + (-0.021 * 5500) + 104.97;
        Assert.Equal((int)(raw + dragPro), InRidePowerParser.PowerForSpeed(30.0, 5.5));

        // No drag offset when there is no spindown.
        Assert.Equal((int)raw, InRidePowerParser.PowerForSpeed(30.0, 0));
        Assert.Equal(0, InRidePowerParser.PowerForSpeed(0, 1.75));
    }

    [Fact]
    public void Power_never_negative_and_saturates_instead_of_overflowing()
    {
        Assert.Equal(0, InRidePowerParser.PowerForSpeed(0.01, 2.0));
        Assert.Equal(int.MaxValue, InRidePowerParser.PowerForSpeed(1e7, 1.75));
    }

    [Fact]
    public void Coasting_is_detected_from_deceleration_and_zeroes_power()
    {
        // Slowing from 40 to 30 km/h in one packet with 10 revs each: alpha = 10 kph * (tpr - ptpr) well above 200.
        var fast = TicksFor(40, 10);
        var slow = TicksFor(30, 10);
        var p = InRidePowerParser.Parse(Wire(Plain(ticks: slow, revs: 10, ticksPrev: fast, revsPrev: 10)));
        Assert.True(p.Coasting);
        Assert.True(p.Alpha > 200);
        Assert.Equal(0, p.PowerWatts);

        // Speeding up: never coasting.
        var q = InRidePowerParser.Parse(Wire(Plain(ticks: fast, revs: 10, ticksPrev: slow, revsPrev: 10)));
        Assert.False(q.Coasting);
        Assert.True(q.PowerWatts > 0);

        // Gentle slowing (40 -> 39.9): below the 200 threshold.
        var r = InRidePowerParser.Parse(Wire(Plain(ticks: TicksFor(39.9, 10), revs: 10, ticksPrev: fast, revsPrev: 10)));
        Assert.False(r.Coasting);
    }

    [Fact]
    public void Pro_flywheel_uses_lower_coasting_threshold()
    {
        var fast = TicksFor(40, 10);
        var slightlySlower = TicksFor(38, 10);
        var normal = InRidePowerParser.Parse(Wire(Plain(ticks: slightlySlower, revs: 10, ticksPrev: fast, revsPrev: 10,
            spindownTicks: (uint)(1.75 * 32768))));
        var pro = InRidePowerParser.Parse(Wire(Plain(ticks: slightlySlower, revs: 10, ticksPrev: fast, revsPrev: 10,
            spindownTicks: (uint)(5.5 * 32768))));
        Assert.True(normal.Alpha > 20 && normal.Alpha < 200, $"alpha={normal.Alpha}");
        Assert.False(normal.Coasting);
        Assert.True(pro.Coasting);
    }

    [Fact]
    public void Configuration_fields_are_little_endian()
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), 602);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 655);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 950);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 327);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)(1.8 * 32768));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 0x10);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 0x08);
        b[16] = 0xDE; b[17] = 0xAD; b[18] = 0xBE; b[19] = 0xEF;

        var c = InRideConfigurationParser.Parse(b);
        Assert.Equal(602, c.CalibrationReadyTicks);
        Assert.Equal(655, c.CalibrationStartTicks);
        Assert.Equal(950, c.CalibrationEndTicks);
        Assert.Equal(327, c.CalibrationDebounceTicks);
        Assert.Equal(1.8, c.CurrentSpindownSeconds, 4);
        Assert.Equal(InRideUpdateRate.Millis500, c.UpdateRateDefault);
        Assert.Equal(0x08, c.UpdateRateCalibrationRaw);
        Assert.False(c.ProFlywheel);
        Assert.Equal("deadbeef", Convert.ToHexStringLower(c.UnusedTail));
    }

    [Fact]
    public void Update_rate_values_are_32hz_intervals()
    {
        Assert.Equal(1.0, InRideUpdateRate.Millis1000.Hz());
        Assert.Equal(2.0, InRideUpdateRate.Millis500.Hz());
        Assert.Equal(4.0, InRideUpdateRate.Millis250.Hz());
        Assert.Equal(TimeSpan.FromMilliseconds(250), InRideUpdateRate.Millis250.Interval());
        Assert.False(InRideUpdateRateExtensions.IsDefined(0x04));
    }

    [Fact]
    public void Calibration_speeds_match_readme_guidance()
    {
        Assert.InRange(InRideProtocol.CalibrationReadySpeedKph * 0.621371, 20.0, 21.0); // "over ~20mph"
        Assert.InRange(InRideProtocol.CalibrationStartSpeedKph * 0.621371, 18.5, 19.5); // "~19mph"
        Assert.InRange(InRideProtocol.CalibrationEndSpeedKph * 0.621371, 12.5, 13.5);   // README says ~11mph; source constant gives 13.1
    }
}
