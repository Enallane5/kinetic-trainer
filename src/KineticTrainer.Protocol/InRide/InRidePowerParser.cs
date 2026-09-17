using System.Buffers.Binary;

namespace KineticTrainer.Protocol.InRide;

/// <summary>
/// Port of the second half of <c>inride_process_power_data</c> plus its helpers
/// (<c>inride_speed_for_ticks</c>, <c>inride_ticks_to_seconds</c>, <c>alpha</c>, <c>power_for_speed</c>,
/// <c>result_for_spindown</c>, <c>inride_has_pro_flywheel</c>) from inRide.c.
/// Arithmetic is kept in the same order as the C source so results match bit-for-bit.
/// </summary>
public static class InRidePowerParser
{
    /// <summary>Decodes a 20-byte Power notification. Throws only on wrong length; use <see cref="InRideProtocol"/> for validated parsing.</summary>
    public static InRidePowerPacket Parse(ReadOnlySpan<byte> data)
    {
        var plain = InRideDeobfuscator.Deobfuscate(data);
        var p = plain.AsSpan();

        // powerData.state = powerBytes[0] & 0x30;  powerData.commandResult = powerBytes[0] & 0x0F;
        var stateBits = (byte)(p[0] & 0x30);
        var commandBits = (byte)(p[0] & 0x0F);

        var interval = (uint)(p[1] | (p[2] << 8) | (p[3] << 16));
        var ticks = BinaryPrimitives.ReadUInt32LittleEndian(p[4..8]);
        var revs = p[8];
        var ticksPrevious = BinaryPrimitives.ReadUInt32LittleEndian(p[9..13]);
        var revsPrevious = p[13];
        var cadenceRaw = BinaryPrimitives.ReadUInt16LittleEndian(p[14..16]);
        var spindownTicks = BinaryPrimitives.ReadUInt32LittleEndian(p[16..20]);

        // powerData.cadenceRPM = cadenceRaw == 0 ? 0 : (0.8652 * ((double)cadenceRaw) + 5.2617);
        var cadenceCalibrated = cadenceRaw == 0 ? 0.0 : (InRideConstants.CadenceSlope * cadenceRaw) + InRideConstants.CadenceIntercept;

        var lastSpindownResultTime = TicksToSeconds(spindownTicks);
        var speedKph = SpeedKphForTicks(ticks, revs);

        // powerData.rollerRPM = 0.0; if (ticks > 0) { seconds = ticks/32768; rollerRPS = revs / seconds; rollerRPM = rollerRPS * 60; }
        var rollerRpm = 0.0;
        if (ticks > 0)
        {
            var seconds = TicksToSeconds(ticks);
            var rollerRps = revs / seconds;
            rollerRpm = rollerRps * 60;
        }

        var speedKphPrev = SpeedKphForTicks(ticksPrevious, revsPrevious);
        var proFlywheel = false;

        var spindownTime = InRideConstants.SpindownDefault;
        if (lastSpindownResultTime >= InRideConstants.SpindownMin && lastSpindownResultTime <= InRideConstants.SpindownMax)
        {
            spindownTime = lastSpindownResultTime;
        }
        else if (lastSpindownResultTime >= InRideConstants.SpindownMinPro && lastSpindownResultTime <= InRideConstants.SpindownMaxPro)
        {
            spindownTime = lastSpindownResultTime;
            proFlywheel = true;
        }

        var rollerResistance = !proFlywheel
            ? 1 - ((spindownTime - InRideConstants.SpindownMin) / (InRideConstants.SpindownMax - InRideConstants.SpindownMin))
            : 1 - ((spindownTime - InRideConstants.SpindownMinPro) / (InRideConstants.SpindownMaxPro - InRideConstants.SpindownMinPro));

        var (alpha, coasting) = Alpha(ticks, revs, speedKph, ticksPrevious, revsPrevious, speedKphPrev, proFlywheel);

        var power = coasting ? 0 : PowerForSpeed(speedKph, spindownTime);

        return new InRidePowerPacket
        {
            RawBytes = data.ToArray(),
            PlainBytes = plain,
            Rotation = (byte)((data[0] & 0xC0) >> 6),
            // KineticInRide.m processPowerData:systemId:error: casts the raw bits straight to the enums
            // ((KineticInRideSensorState)cData.state, (KineticInRideSensorCommandResult)cData.commandResult),
            // so an undefined command nibble (e.g. 0x05) is preserved as-is rather than mapped to None.
            State = (InRideSensorState)stateBits,
            StateBits = stateBits,
            CommandResult = (InRideCommandResult)commandBits,
            CommandResultBits = commandBits,
            Interval = interval,
            Ticks = ticks,
            Revolutions = revs,
            TicksPrevious = ticksPrevious,
            RevolutionsPrevious = revsPrevious,
            CadenceRaw = cadenceRaw,
            SpindownTicks = spindownTicks,
            PowerWatts = power,
            SpeedKph = speedKph,
            SpeedKphPrevious = speedKphPrev,
            RollerRpm = rollerRpm,
            CadenceCalibratedRpm = cadenceCalibrated,
            Coasting = coasting,
            Alpha = alpha,
            SpindownTimeSeconds = spindownTime,
            LastSpindownResultSeconds = lastSpindownResultTime,
            RollerResistance = rollerResistance,
            ProFlywheel = proFlywheel,
            CalibrationResult = ResultForSpindown(lastSpindownResultTime),
        };
    }

    /// <summary>inride_ticks_to_seconds: ticks / 32768.0</summary>
    public static double TicksToSeconds(uint ticks) => ticks / InRideConstants.SensorHz;

    /// <summary>inride_speed_for_ticks: (20012.256849 * revs) / ticks, or 0 when either is 0.</summary>
    public static double SpeedKphForTicks(uint ticks, byte revs)
    {
        if (ticks == 0 || revs <= 0)
        {
            return 0;
        }
        return (InRideConstants.SpeedKphTicksConstant * revs) / ticks;
    }

    /// <summary>inride_has_pro_flywheel: spindown within [4.7, 6.5] s.</summary>
    public static bool HasProFlywheel(double spindown) => spindown >= InRideConstants.SpindownMinPro && spindown <= InRideConstants.SpindownMaxPro;

    /// <summary>
    /// inRide.c <c>alpha()</c>. Coasting is detected from deceleration: when ticks-per-revolution grew since the previous
    /// reading, alpha = (previous speed - speed) x (tpr - previous tpr); coasting when alpha exceeds 200 (normal) or 20 (pro flywheel).
    /// Note: a zero revolution count yields infinity/NaN exactly as the C double division does; comparisons then behave identically.
    /// </summary>
    internal static (double Alpha, bool Coasting) Alpha(uint ticks, byte revs, double speedKph, uint ticksPrevious, byte revsPrevious,
        double speedKphPrevious, bool proFlywheel)
    {
        var alpha = 0.0;
        var coasting = false;
        if (ticks > 0 && ticksPrevious > 0)
        {
            var tpr = ticks / (double)revs;
            var ptpr = ticksPrevious / (double)revsPrevious;
            var dtpr = tpr - ptpr;
            if (dtpr > 0)
            {
                var deltaSpeed = speedKphPrevious - speedKph;
                alpha = deltaSpeed * dtpr;
                if (alpha > InRideConstants.CoastingAlphaThreshold && !proFlywheel)
                {
                    coasting = true;
                }
                else if (alpha > InRideConstants.CoastingAlphaThresholdPro && proFlywheel)
                {
                    coasting = true;
                }
            }
        }
        return (alpha, coasting);
    }

    /// <summary>
    /// inRide.c <c>power_for_speed</c>: watts = 5.244820 mph + 0.019168 mph^3, plus a drag offset that depends on the
    /// spindown time (tyre pressure on the roller) and flywheel type. The <c>alpha</c> and <c>revolutions</c> parameters of the
    /// C function are unused there and omitted here. Truncated toward zero like the C <c>(int)</c> cast; values beyond
    /// <see cref="int.MaxValue"/> (physically impossible) saturate instead of relying on undefined C behaviour.
    /// </summary>
    public static int PowerForSpeed(double kph, double spindown)
    {
        var mph = kph * InRideConstants.KphToMph;
        var rawPower = (InRideConstants.PowerLinear * mph) + (InRideConstants.PowerCubic * (mph * mph * mph));
        double dragOffset;
        if (spindown > 0 && rawPower > 0)
        {
            var proFlywheel = HasProFlywheel(spindown);
            var spindownTimeMs = spindown * 1000.0;
            var dragOffsetSlope = proFlywheel ? InRideConstants.DragOffsetSlopePro : InRideConstants.DragOffsetSlope;
            var dragOffsetPowerSlope = proFlywheel ? InRideConstants.DragOffsetPowerSlopePro : InRideConstants.DragOffsetPowerSlope;
            var yIntercept = proFlywheel ? InRideConstants.YInterceptPro : InRideConstants.YIntercept;
            dragOffset = (dragOffsetPowerSlope * spindownTimeMs * rawPower * 0.00001) + (dragOffsetSlope * spindownTimeMs) + yIntercept;
        }
        else
        {
            dragOffset = 0;
        }
        var power = rawPower + dragOffset;
        if (power < 0)
        {
            power = 0;
        }
        return power >= int.MaxValue ? int.MaxValue : (int)power;
    }

    /// <summary>inRide.c <c>result_for_spindown</c>.</summary>
    public static InRideCalibrationResult ResultForSpindown(double time)
    {
        if (time >= InRideConstants.SpindownMin && time <= InRideConstants.SpindownMax)
        {
            return InRideCalibrationResult.Success;
        }
        if (time >= InRideConstants.SpindownMinPro && time <= InRideConstants.SpindownMaxPro)
        {
            return InRideCalibrationResult.Success;
        }
        if (time < InRideConstants.SpindownMin)
        {
            return InRideCalibrationResult.TooFast;
        }
        if (time > InRideConstants.SpindownMaxPro)
        {
            return InRideCalibrationResult.TooSlow;
        }
        return InRideCalibrationResult.Middle;
    }
}
