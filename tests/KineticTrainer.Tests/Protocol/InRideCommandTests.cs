using KineticTrainer.Protocol.InRide;
using Xunit;

namespace KineticTrainer.Tests.Protocol;

public class InRideCommandTests
{
    // systemId[3] = 0x67 -> 103 % 6 = 1 -> systemId[1] = 0x23 (low byte)
    // systemId[5] = 0xAB -> 171 % 6 = 3 -> systemId[3] = 0x67 (high byte)   => key 0x6723, wire "23 67"
    private static readonly byte[] SystemId = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB];

    [Fact]
    public void Command_key_is_built_from_system_id_bytes_3_and_5()
    {
        Assert.Equal(0x6723, InRideCommands.CommandKey(SystemId));
        Assert.Equal(0x0000, InRideCommands.CommandKey(new byte[6]));
        Assert.Equal(0xFFFF, InRideCommands.CommandKey([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void Start_and_stop_calibration_are_three_bytes()
    {
        Assert.Equal("236703", Convert.ToHexStringLower(InRideCommands.StartCalibration(SystemId)));
        Assert.Equal("236704", Convert.ToHexStringLower(InRideCommands.StopCalibration(SystemId)));
    }

    [Theory]
    [InlineData(InRideUpdateRate.Millis1000, "2367015a028f02b603470120000800")]
    [InlineData(InRideUpdateRate.Millis500, "2367015a028f02b603470110000800")]
    [InlineData(InRideUpdateRate.Millis250, "2367015a028f02b603470108000800")]
    public void Configure_sensor_is_fifteen_bytes_with_kinetic_calibration_constants(InRideUpdateRate rate, string expectedHex)
    {
        var cmd = InRideCommands.ConfigureSensor(SystemId, rate);
        Assert.Equal(15, cmd.Length);
        Assert.Equal(expectedHex, Convert.ToHexStringLower(cmd));
    }

    [Fact]
    public void Set_spindown_time_encodes_ticks_little_endian()
    {
        // 1.75 s * 32768 = 57344 = 0x0000E000
        Assert.Equal("23670500e00000", Convert.ToHexStringLower(InRideCommands.SetSpindownTime(SystemId, 1.75)));
        Assert.Equal("2367050000feff", Convert.ToHexStringLower(InRideCommands.SetSpindownTime(SystemId, 0xFFFE0000 / 32768.0)));
    }

    [Fact]
    public void Set_name_appends_utf8()
    {
        Assert.Equal("236702696e52696465", Convert.ToHexStringLower(InRideCommands.SetPeripheralName(SystemId, "inRide")));
        Assert.Throws<ArgumentException>(() => InRideCommands.SetPeripheralName(SystemId, "ab"));
        Assert.Throws<ArgumentException>(() => InRideCommands.SetPeripheralName(SystemId, "123456789"));
    }

    [Fact]
    public void System_id_to_string_is_lowercase_hex()
    {
        Assert.Equal("0123456789ab", InRideProtocol.SystemIdToString(SystemId));
    }
}
