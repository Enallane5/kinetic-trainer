using KineticTrainer.Protocol.InRide;
using Xunit;

namespace KineticTrainer.Tests.Protocol;

/// <summary>
/// Bit-for-bit comparison of the C# port against Kinetic's compiled C reference (3,700+ steps, including
/// stateful cadence smoothing across realistic ride sequences).
/// </summary>
public class InRideReferenceVectorTests
{
    public static IEnumerable<object[]> GroupNames() =>
        ReferenceVectors.Instance.Groups.Select(g => new object[] { g.Name });

    [Fact]
    public void Vectors_come_from_kinetic_source()
    {
        Assert.Contains("sensors-swift-kinetic", ReferenceVectors.Instance.Source);
        Assert.True(ReferenceVectors.Instance.Groups.Sum(g => g.Steps.Count) > 3000);
    }

    [Theory]
    [MemberData(nameof(GroupNames))]
    public void Power_packets_match_reference(string groupName)
    {
        var group = ReferenceVectors.Instance.Groups.Single(g => g.Name == groupName);
        var protocol = new InRideProtocol();
        var systemId = new byte[] { 1, 2, 3, 4, 5, 6 };
        var powerSteps = 0;

        foreach (var step in group.Steps)
        {
            switch (step.Op)
            {
                case "R":
                    protocol.ResetCadenceSmoothing();
                    break;
                case "P":
                {
                    Assert.Null(step.Error);
                    var data = Convert.FromHexString(step.In!);
                    var result = protocol.ProcessPowerData(data, systemId, step.T);
                    Assert.True(result.IsSuccess, result.Message);
                    var p = result.Value.Packet;
                    var why = $"packet {step.In} @ t={step.T}";

                    Assert.True(step.State == p.StateBits, $"state {why}: expected {step.State} got {p.StateBits}");
                    Assert.True(step.CommandResult == p.CommandResultBits, $"commandResult {why}");
                    Assert.True(step.Power == p.PowerWatts, $"power {why}: expected {step.Power} got {p.PowerWatts}");
                    AssertExact(step.SpeedKph, p.SpeedKph, "speedKPH", why);
                    AssertExact(step.RollerRpm, p.RollerRpm, "rollerRPM", why);
                    AssertExact(step.CadenceCalibrated, p.CadenceCalibratedRpm, "cadence (C)", why);
                    AssertExact(step.CadenceRpm, result.Value.CadenceRpm, "cadence (SDK smoothed)", why);
                    Assert.True(step.Coasting == p.Coasting, $"coasting {why}");
                    AssertExact(step.SpindownTime, p.SpindownTimeSeconds, "spindownTime", why);
                    AssertExact(step.LastSpindownResultTime, p.LastSpindownResultSeconds, "lastSpindownResultTime", why);
                    AssertExact(step.RollerResistance, p.RollerResistance, "rollerResistance", why);
                    Assert.True(step.ProFlywheel == p.ProFlywheel, $"proFlywheel {why}");
                    Assert.True(step.CalibrationResult == (int)p.CalibrationResult, $"calibrationResult {why}");
                    powerSteps++;
                    break;
                }
            }
        }

        if (group.Kind.StartsWith("power"))
        {
            Assert.True(powerSteps > 0);
        }
    }

    [Fact]
    public void Configuration_packets_match_reference()
    {
        var group = ReferenceVectors.Instance.Groups.Single(g => g.Kind == "config");
        var count = 0;
        foreach (var step in group.Steps.Where(s => s.Op == "C"))
        {
            var result = InRideProtocol.ProcessConfigurationData(Convert.FromHexString(step.In!));
            Assert.True(result.IsSuccess, result.Message);
            var c = result.Value;
            Assert.Equal(step.CalibrationReady, c.CalibrationReadyTicks);
            Assert.Equal(step.CalibrationStart, c.CalibrationStartTicks);
            Assert.Equal(step.CalibrationEnd, c.CalibrationEndTicks);
            Assert.Equal(step.CalibrationDebounce, c.CalibrationDebounceTicks);
            AssertExact(step.CurrentSpindownTime, c.CurrentSpindownSeconds, "currentSpindownTime", step.In!);
            Assert.Equal(step.UpdateRateDefault, c.UpdateRateDefaultRaw);
            Assert.Equal(step.UpdateRateCalibration, c.UpdateRateCalibrationRaw);
            Assert.Equal(step.ProFlywheel, c.ProFlywheel);
            count++;
        }
        Assert.True(count > 100);
    }

    [Fact]
    public void Control_point_commands_match_reference()
    {
        var group = ReferenceVectors.Instance.Groups.Single(g => g.Kind == "commands");
        var count = 0;
        foreach (var step in group.Steps)
        {
            var systemId = Convert.FromHexString(step.SystemId!);
            var actual = step.Op switch
            {
                "K" => InRideProtocol.ConfigureSensorCommandData(systemId, (InRideUpdateRate)(ushort)step.Arg),
                "S" => InRideProtocol.StartCalibrationCommandData(systemId),
                "T" => InRideProtocol.StopCalibrationCommandData(systemId),
                "D" => InRideProtocol.SetSpindownTimeCommandData(systemId, step.Arg),
                _ => throw new InvalidOperationException(step.Op),
            };
            Assert.True(actual.IsSuccess, actual.Message);
            Assert.True(step.Out == Convert.ToHexStringLower(actual.Value),
                $"{step.Op} sysid={step.SystemId} arg={step.Arg}: expected {step.Out} got {Convert.ToHexStringLower(actual.Value)}");
            count++;
        }
        Assert.True(count > 200);
    }

    private static void AssertExact(double expected, double actual, string field, string context)
    {
        // Exact double equality: the C# arithmetic is ordered like the C source, so results must be identical bits.
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual)
                    || (double.IsNaN(expected) && double.IsNaN(actual)),
            $"{field} {context}: expected {expected:R} got {actual:R}");
    }
}
