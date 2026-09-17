namespace KineticTrainer.Protocol.InRide;

/// <summary>
/// Port of <c>+[KineticInRide adjustCadence:timestamp:]</c> (KineticInRide.m), the "SDK Adjustment of Cadence"
/// applied on top of the C parser. It is a weighted rolling average over the last <c>bufferSize</c> (3) samples,
/// the newest weighted <c>weight</c> (2) times, reset when cadence hits zero or the samples are more than 2 s apart.
///
/// Two quirks of the original are reproduced deliberately, because the goal is to match Kinetic Fit's numbers:
///  1. The value fed in is the calibrated cadence truncated to an integer (<c>powerData.cadenceRaw = cData.cadenceRPM</c>
///     assigns a double to a <c>uint16_t</c>).
///  2. The averaging loop always sums <c>bufferSize - 1</c> older slots, even right after a reset. Slots are zero
///     when the process starts (so the first sample after start reads at half value) and otherwise hold stale
///     values from before the reset.
/// Kinetic's implementation is process-global static state; this port is an instance so each connection gets its own.
/// </summary>
public sealed class InRideCadenceSmoother
{
    public const int BufferSizeMax = 10;
    public const int BufferSizeDefault = 3;
    public const int BufferWeightDefault = 2;

    private readonly (double Timestamp, ushort CadenceRpm)[] _buffer = new (double, ushort)[BufferSizeMax + 1];
    private readonly int _bufferSize;
    private readonly int _bufferWeight;
    private int _count;

    public InRideCadenceSmoother(int bufferSize = BufferSizeDefault, int weight = BufferWeightDefault)
    {
        _bufferSize = Math.Min(BufferSizeMax, Math.Max(1, bufferSize));
        _bufferWeight = Math.Max(1, weight);
    }

    /// <summary>Feeds the calibrated cadence of one packet and returns the smoothed value the SDK would report.</summary>
    /// <param name="cadenceCalibratedRpm">The parser's <see cref="InRidePowerPacket.CadenceCalibratedRpm"/>.</param>
    /// <param name="timestampSeconds">Packet time in seconds (any epoch; only differences matter).</param>
    public double Adjust(double cadenceCalibratedRpm, double timestampSeconds)
    {
        // KineticInRide.m processPowerData:systemId:error: line "powerData.cadenceRaw = cData.cadenceRPM;"
        // assigns the C double to a uint16_t property, i.e. truncation toward zero. The calibrated value is always
        // within 0..56706 (0.8652 * 65535 + 5.2617), so the range guards below never change a real result.
        var crankRpm = cadenceCalibratedRpm >= ushort.MaxValue ? ushort.MaxValue
            : cadenceCalibratedRpm <= 0 ? (ushort)0
            : (ushort)cadenceCalibratedRpm;
        return AdjustRaw(crankRpm, timestampSeconds);
    }

    /// <summary>Line-for-line port of <c>+[KineticInRide adjustCadence:timestamp:]</c>.</summary>
    internal double AdjustRaw(ushort crankRpm, double timestamp)
    {
        if (crankRpm == 0)
        {
            _count = 0;
            return 0;
        }

        if (_count > 0 && timestamp - _buffer[0].Timestamp > 2)
        {
            _count = 0;
        }

        // shift cadence values down ...
        for (var i = _count; i > 0; i--)
        {
            _buffer[i] = _buffer[i - 1];
        }
        _buffer[0] = (timestamp, crankRpm);

        _count = Math.Min(_bufferSize, _count + 1);

        double rollingRpm = crankRpm * _bufferWeight;
        for (var i = 1; i < _bufferSize; i++)
        {
            rollingRpm += _buffer[i].CadenceRpm;
        }
        rollingRpm /= _bufferSize + _bufferWeight - 1;

        return rollingRpm;
    }

    /// <summary>Equivalent to a fresh process: count 0 and all slots zero.</summary>
    public void Reset()
    {
        Array.Clear(_buffer);
        _count = 0;
    }
}
