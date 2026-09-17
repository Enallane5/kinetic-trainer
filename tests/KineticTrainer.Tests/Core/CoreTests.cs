using KineticTrainer.Core.Settings;
using KineticTrainer.Core.Streaming;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Core.Timing;
using KineticTrainer.Core.Units;
using Xunit;

namespace KineticTrainer.Tests.Core;

public class BroadcastTests
{
    [Fact]
    public void Publish_never_blocks_and_drops_oldest_for_slow_subscriber()
    {
        var b = new Broadcast<int>();
        using var slow = b.Subscribe("slow", capacity: 4);
        for (var i = 0; i < 100; i++)
        {
            b.Publish(i);
        }
        Assert.Equal(96, slow.DroppedCount);
        var got = new List<int>();
        while (slow.Reader.TryRead(out var v))
        {
            got.Add(v);
        }
        Assert.Equal([96, 97, 98, 99], got);
    }

    [Fact]
    public void Disposed_subscription_stops_receiving()
    {
        var b = new Broadcast<int>();
        var sub = b.Subscribe("x");
        b.Publish(1);
        sub.Dispose();
        b.Publish(2);
        Assert.Equal(0, b.SubscriberCount);
        Assert.True(sub.Reader.TryRead(out var first));
        Assert.Equal(1, first);
        Assert.False(sub.Reader.TryRead(out _));
    }

    [Fact]
    public void Telemetry_hub_stamps_sequence_and_keeps_latest()
    {
        var hub = new TelemetryHub();
        using var sub = hub.Subscribe("ui");
        hub.Publish(TrainerTelemetry.NoData(DateTimeOffset.UnixEpoch, TrainerConnectionState.Searching));
        hub.Publish(TrainerTelemetry.NoData(DateTimeOffset.UnixEpoch, TrainerConnectionState.Connected));
        Assert.Equal(2, hub.Latest!.Sequence);
        Assert.Equal(TrainerConnectionState.Connected, hub.Latest.ConnectionState);
        Assert.True(sub.Reader.TryRead(out var first));
        Assert.Equal(1, first.Sequence);
    }
}

public class TimingTests
{
    [Fact]
    public void Backoff_grows_and_caps()
    {
        var b = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), factor: 2, jitter: 0);
        Assert.Equal(TimeSpan.FromSeconds(1), b.Next());
        Assert.Equal(TimeSpan.FromSeconds(2), b.Next());
        Assert.Equal(TimeSpan.FromSeconds(4), b.Next());
        Assert.Equal(TimeSpan.FromSeconds(8), b.Next());
        Assert.Equal(TimeSpan.FromSeconds(10), b.Next());
        Assert.Equal(TimeSpan.FromSeconds(10), b.Next());
        b.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), b.Next());
    }

    [Fact]
    public void Backoff_jitter_stays_within_bounds()
    {
        var b = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitter: 0.25, random: new Random(1));
        for (var i = 0; i < 50; i++)
        {
            var d = b.Next();
            Assert.InRange(d.TotalSeconds, 0.75, 30.0);
        }
    }

    [Fact]
    public void Rate_estimator_reports_hz_over_window()
    {
        var r = new RateEstimator(TimeSpan.FromSeconds(5));
        var t0 = DateTimeOffset.UnixEpoch;
        Assert.Null(r.RateHz(t0));
        for (var i = 0; i <= 8; i++)
        {
            r.Mark(t0.AddMilliseconds(250 * i));
        }
        Assert.Equal(4.0, r.RateHz(t0.AddSeconds(2))!.Value, 6);
        Assert.Equal(TimeSpan.FromMilliseconds(250), r.MedianInterval(t0.AddSeconds(2)));
        Assert.Null(r.RateHz(t0.AddSeconds(30)));
    }
}

public class UnitTests
{
    [Fact]
    public void Roller_speed_uses_circumference_times_rpm()
    {
        var mps = UnitConversions.RollerSpeedMetersPerSecond(3000, AppSettings.KineticRollerCircumferenceMeters);
        Assert.Equal(3000 / 60.0 * 0.16964600329384882, mps, 12);
        Assert.Equal(mps * 3.6, UnitConversions.ToKph(mps), 12);
        Assert.Equal(mps * 2.2369362920544, UnitConversions.ToMph(mps), 9);
    }

    [Fact]
    public void Kinetic_default_roller_matches_speed_constant()
    {
        // inRide.c: kph = 20012.256849 * revs / ticks  <=>  circumference = 20012.256849 / (32768 * 3.6) metres
        Assert.Equal(20012.256849 / (32768 * 3.6), AppSettings.KineticRollerCircumferenceMeters, 9);
    }

    [Fact]
    public void Settings_validation_rejects_nonsense()
    {
        Assert.Empty(new AppSettings().Validate());
        Assert.NotEmpty(new AppSettings { RollerCircumferenceMeters = 0 }.Validate());
        Assert.NotEmpty(new AppSettings { SimulatorBridgePort = 80 }.Validate());
    }
}
