using KineticTrainer.Protocol.InRide;
using Xunit;

namespace KineticTrainer.Tests.Protocol;

public class InRideMalformedPacketTests
{
    private static readonly byte[] SystemId = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(40)]
    public void Power_packet_with_wrong_length_is_rejected_with_code_202(int length)
    {
        var protocol = new InRideProtocol();
        var result = protocol.ProcessPowerData(new byte[length], SystemId, 0);
        Assert.False(result.IsSuccess);
        Assert.Equal(InRideErrorCode.InvalidData, result.Error);
        Assert.Equal(202, (int)result.Error);
        Assert.Contains($"got {length}", result.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    public void Power_packet_with_wrong_system_id_length_is_rejected_with_code_201(int length)
    {
        var protocol = new InRideProtocol();
        var result = protocol.ProcessPowerData(new byte[20], new byte[length], 0);
        Assert.False(result.IsSuccess);
        Assert.Equal(InRideErrorCode.InvalidSystemId, result.Error);
        Assert.Equal(201, (int)result.Error);
    }

    [Fact]
    public void System_id_validation_can_be_bypassed_for_diagnostics()
    {
        var protocol = new InRideProtocol();
        var result = protocol.ProcessPowerData(new byte[20], new byte[8], 0, ignoreSystemIdValidation: true);
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(21)]
    public void Configuration_packet_with_wrong_length_is_rejected_with_code_202(int length)
    {
        var result = InRideProtocol.ProcessConfigurationData(new byte[length]);
        Assert.False(result.IsSuccess);
        Assert.Equal(InRideErrorCode.InvalidData, result.Error);
    }

    [Fact]
    public void Any_20_byte_pattern_parses_without_throwing()
    {
        var protocol = new InRideProtocol();
        var rng = new Random(7);
        var buffer = new byte[20];
        for (var i = 0; i < 20_000; i++)
        {
            rng.NextBytes(buffer);
            var result = protocol.ProcessPowerData(buffer, SystemId, i * 0.25);
            Assert.True(result.IsSuccess);
            var p = result.Value.Packet;
            Assert.True(p.PowerWatts >= 0);
            Assert.True(p.RollerRpm >= 0);
            Assert.True(p.SpeedKph >= 0);
            Assert.False(double.IsNaN(result.Value.CadenceRpm));
        }
        Array.Fill(buffer, (byte)0xFF);
        Assert.True(protocol.ProcessPowerData(buffer, SystemId, 0).IsSuccess);
        Array.Clear(buffer);
        Assert.True(protocol.ProcessPowerData(buffer, SystemId, 0).IsSuccess);
    }

    [Fact]
    public void Commands_reject_bad_system_ids_and_names()
    {
        Assert.Equal(InRideErrorCode.InvalidSystemId, InRideProtocol.StartCalibrationCommandData(new byte[5]).Error);
        Assert.Equal(InRideErrorCode.InvalidSystemId, InRideProtocol.ConfigureSensorCommandData([], InRideUpdateRate.Millis1000).Error);
        Assert.Equal(InRideErrorCode.InvalidName, InRideProtocol.SetPeripheralNameCommandData(SystemId, "ab").Error);
        Assert.Equal(InRideErrorCode.InvalidName, InRideProtocol.SetPeripheralNameCommandData(SystemId, "abcdefghi").Error);
        Assert.Equal(203, (int)InRideProtocol.SetPeripheralNameCommandData(SystemId, "ab").Error);
        Assert.Equal(InRideErrorCode.InvalidData, InRideProtocol.SetSpindownTimeCommandData(SystemId, -1).Error);
        Assert.Equal(InRideErrorCode.InvalidData, InRideProtocol.SetSpindownTimeCommandData(SystemId, double.NaN).Error);
    }
}
