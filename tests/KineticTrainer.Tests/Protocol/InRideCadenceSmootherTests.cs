using KineticTrainer.Protocol.InRide;
using Xunit;

namespace KineticTrainer.Tests.Protocol;

/// <summary>Behaviour of <c>+[KineticInRide adjustCadence:timestamp:]</c>, including its quirks.</summary>
public class InRideCadenceSmootherTests
{
    [Fact]
    public void Steady_cadence_converges_to_itself_after_three_samples()
    {
        var s = new InRideCadenceSmoother();
        Assert.Equal(90 * 2 / 4.0, s.AdjustRaw(90, 0.0));        // (2*90 + 0 + 0) / 4  = 45  (first-sample quirk)
        Assert.Equal((2 * 90 + 90) / 4.0, s.AdjustRaw(90, 1.0)); // (2*90 + 90 + 0) / 4 = 67.5
        Assert.Equal(90, s.AdjustRaw(90, 2.0));                  // (2*90 + 90 + 90) / 4 = 90
        Assert.Equal(90, s.AdjustRaw(90, 3.0));
    }

    [Fact]
    public void Newest_sample_is_weighted_twice()
    {
        var s = new InRideCadenceSmoother();
        s.AdjustRaw(80, 0);
        s.AdjustRaw(80, 1);
        Assert.Equal((2 * 100 + 80 + 80) / 4.0, s.AdjustRaw(100, 2));
    }

    [Fact]
    public void Zero_cadence_returns_zero_and_resets_count_but_keeps_stale_slots()
    {
        var s = new InRideCadenceSmoother();
        s.AdjustRaw(80, 0);
        s.AdjustRaw(80, 1);
        s.AdjustRaw(80, 2);
        Assert.Equal(0, s.AdjustRaw(0, 3));
        // Kinetic quirk: the loop still sums slots 1 and 2, which hold the pre-reset values.
        Assert.Equal((2 * 90 + 80 + 80) / 4.0, s.AdjustRaw(90, 4));
    }

    [Fact]
    public void Gap_over_two_seconds_resets_count()
    {
        var s = new InRideCadenceSmoother();
        s.AdjustRaw(70, 0);
        s.AdjustRaw(70, 1);
        s.AdjustRaw(70, 2);
        // 2.5 s after the newest sample: count resets to 0, new sample lands in slot 0, slots 1-2 still hold 70.
        Assert.Equal((2 * 100 + 70 + 70) / 4.0, s.AdjustRaw(100, 4.5));
        // Exactly 2 s is not "> 2": no reset.
        var t = new InRideCadenceSmoother();
        t.AdjustRaw(70, 0);
        Assert.Equal((2 * 100 + 70) / 4.0, t.AdjustRaw(100, 2.0));
    }

    [Fact]
    public void Reset_clears_slots_like_a_fresh_process()
    {
        var s = new InRideCadenceSmoother();
        s.AdjustRaw(80, 0);
        s.AdjustRaw(80, 1);
        s.Reset();
        Assert.Equal(45, s.AdjustRaw(90, 2));
    }

    [Fact]
    public void Calibrated_double_is_truncated_to_integer_before_smoothing()
    {
        var s = new InRideCadenceSmoother();
        // 0.8652 * 100 + 5.2617 = 91.7817 -> 91
        Assert.Equal(91 * 2 / 4.0, s.Adjust(91.7817, 0));
        Assert.Equal(0, s.Adjust(0, 1));
        Assert.Equal(0, s.Adjust(-5, 2));
    }
}
