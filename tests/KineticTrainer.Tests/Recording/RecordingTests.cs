using System.Xml.Linq;
using KineticTrainer.Core.Rides;
using KineticTrainer.Core.Statistics;
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Recording;
using KineticTrainer.Recording.Fit;
using KineticTrainer.Recording.Tcx;
using Xunit;
using static KineticTrainer.Recording.Fit.FitProfile;

namespace KineticTrainer.Tests.Recording;

public static class RideFixtures
{
    public static readonly DateTimeOffset Start = new(2026, 9, 16, 22, 0, 0, TimeSpan.Zero);

    /// <summary>A 2 Hz ride: power ramps 100->300 W, cadence 90, speed 8 m/s, for <paramref name="seconds"/> seconds.</summary>
    public static RideRecording Ride(int seconds = 120, bool simulated = false)
    {
        var hub = new TelemetryHub();
        using var recorder = new RideRecorder(hub);
        recorder.Start(Start);
        for (var i = 0; i < seconds * 2; i++)
        {
            var t = Start.AddMilliseconds(500 * i);
            recorder.Observe(new TrainerTelemetry
            {
                Timestamp = t, ConnectionState = TrainerConnectionState.Connected, IsDataValid = true,
                PowerWatts = 100 + (int)Math.Round(200.0 * i / (seconds * 2 - 1)), CadenceRpm = 90, SpeedMetersPerSecond = 8.0,
                SensorName = "inRide 48343", IsSimulated = simulated,
            });
        }
        return recorder.Stop(Start.AddSeconds(seconds))!;
    }
}

public class RideRecorderTests
{
    [Fact]
    public void Integrates_distance_over_consecutive_samples_and_ignores_long_gaps()
    {
        var hub = new TelemetryHub();
        using var r = new RideRecorder(hub);
        var t0 = RideFixtures.Start;
        r.Start(t0);
        Sample(r, t0, 10);
        Sample(r, t0.AddSeconds(1), 10);   // +10 m
        Sample(r, t0.AddSeconds(2), 10);   // +10 m
        Sample(r, t0.AddSeconds(12), 10);  // 10 s gap: not integrated
        Sample(r, t0.AddSeconds(13), 10);  // +10 m
        Assert.Equal(30, r.DistanceMeters, 6);
        var ride = r.Stop(t0.AddSeconds(14))!;
        Assert.Equal(5, ride.Samples.Count);
        Assert.Equal(14, ride.Summary.ElapsedSeconds, 6);
        Assert.Equal(30, ride.Summary.DistanceMeters, 6);
    }

    [Fact]
    public void Invalid_samples_are_not_recorded_and_stop_without_start_is_null()
    {
        var hub = new TelemetryHub();
        using var r = new RideRecorder(hub);
        Assert.Null(r.Stop());
        r.Start(RideFixtures.Start);
        r.Observe(TrainerTelemetry.NoData(RideFixtures.Start, TrainerConnectionState.Reconnecting));
        Assert.Equal(0, r.SampleCount);
        var ride = r.Stop(RideFixtures.Start.AddSeconds(5))!;
        Assert.Empty(ride.Samples);
        Assert.Equal(5, ride.Summary.ElapsedSeconds, 6);
        Assert.Null(ride.Summary.AveragePowerWatts);
    }

    [Fact]
    public void Negative_power_extreme_cadence_and_early_or_backwards_samples_are_normalised()
    {
        var hub = new TelemetryHub();
        using var r = new RideRecorder(hub);
        var t0 = RideFixtures.Start;
        r.Start(t0);
        r.Observe(new TrainerTelemetry { Timestamp = t0.AddSeconds(-1), ConnectionState = TrainerConnectionState.Connected, IsDataValid = true, PowerWatts = 999 });
        r.Observe(new TrainerTelemetry { Timestamp = t0.AddSeconds(1), ConnectionState = TrainerConnectionState.Connected, IsDataValid = true, PowerWatts = -50, CadenceRpm = 300, SpeedMetersPerSecond = 10 });
        r.Observe(new TrainerTelemetry { Timestamp = t0.AddSeconds(2), ConnectionState = TrainerConnectionState.Connected, IsDataValid = true, PowerWatts = 100, SpeedMetersPerSecond = double.NaN });
        r.Observe(new TrainerTelemetry { Timestamp = t0.AddSeconds(1.5), ConnectionState = TrainerConnectionState.Connected, IsDataValid = true, SpeedMetersPerSecond = 10 });
        r.Observe(new TrainerTelemetry { Timestamp = t0.AddSeconds(3), ConnectionState = TrainerConnectionState.Connected, IsDataValid = true, SpeedMetersPerSecond = 10 });
        var ride = r.Stop(t0.AddSeconds(4))!;
        Assert.Equal(4, ride.Samples.Count);               // the pre-start sample is ignored
        Assert.Equal(0, ride.Samples[0].PowerWatts);       // negative power recorded as 0 W
        Assert.Equal(254, ride.Samples[0].CadenceRpm);     // cadence clamped
        Assert.Null(ride.Samples[1].SpeedMetersPerSecond); // NaN dropped
        // Right-endpoint integration: (1,2] ends on a sample with no speed so is not credited; the out-of-order 1.5 s
        // sample does not rewind the origin; (2,3] at 10 m/s gives 10 m.
        Assert.Equal(10, ride.Summary.DistanceMeters, 6);
        Assert.Equal(100, ride.Summary.MaxPowerWatts);

        var tcx = TcxWriter.Write(ride);
        Assert.DoesNotContain(">-50<", tcx);
        Assert.DoesNotContain("NaN", tcx);
        Assert.Contains("<Cadence>254</Cadence>", tcx);
    }

    [Fact]
    public async Task Records_from_the_telemetry_stream()
    {
        var hub = new TelemetryHub();
        using var r = new RideRecorder(hub);
        r.Start();
        hub.Publish(new TrainerTelemetry { Timestamp = DateTimeOffset.UtcNow, ConnectionState = TrainerConnectionState.Connected, IsDataValid = true, PowerWatts = 150 });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (r.SampleCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.Equal(1, r.SampleCount);
        Assert.Equal(150, r.AveragePowerWatts);
    }

    private static void Sample(RideRecorder r, DateTimeOffset t, double speed) =>
        r.Observe(new TrainerTelemetry { Timestamp = t, ConnectionState = TrainerConnectionState.Connected, IsDataValid = true, SpeedMetersPerSecond = speed, PowerWatts = 100 });
}

public class RideStatisticsTests
{
    private static RideSample S(double elapsed, int? p, double? c = 90, double? v = 8, double d = 0) =>
        new(RideFixtures.Start.AddSeconds(elapsed), elapsed, p, c, v, d);

    [Fact]
    public void Constant_power_gives_equal_average_and_normalized_power()
    {
        var samples = Enumerable.Range(0, 120).Select(i => S(i * 0.5, 200, d: i * 4)).ToList();
        var s = RideStatisticsCalculator.Compute(samples, RideFixtures.Start, RideFixtures.Start.AddSeconds(60));
        Assert.Equal(200, s.AveragePowerWatts!.Value, 6);
        Assert.Equal(200, s.NormalizedPowerWatts!.Value, 6);
        Assert.Equal(200, s.MaxPowerWatts);
        Assert.Equal(90, s.AverageCadenceRpm!.Value, 6);
        Assert.Equal(8, s.MaxSpeedMetersPerSecond!.Value, 6);
        Assert.Equal(476.0 / 60, s.AverageSpeedMetersPerSecond!.Value, 6); // last distance / elapsed
        Assert.Equal(60, s.MovingSeconds, 6);
        Assert.Equal(200 * 60 / 1000.0, s.WorkKilojoules!.Value, 6);
    }

    [Fact]
    public void Normalized_power_exceeds_average_for_variable_power_and_needs_30_seconds()
    {
        var alternating = Enumerable.Range(0, 120).Select(i => (i / 30) % 2 == 0 ? 100.0 : 300.0).ToList();
        var np = NormalizedPowerCalculator.Compute(alternating)!.Value;
        Assert.True(np > 200 && np < 300, $"NP={np}");
        Assert.Null(NormalizedPowerCalculator.Compute(Enumerable.Repeat(200.0, 29).ToList()));
        Assert.Equal(200, NormalizedPowerCalculator.Compute(Enumerable.Repeat(200.0, 30).ToList())!.Value, 9);
    }

    [Fact]
    public void Resampling_carries_short_gaps_and_drops_long_ones()
    {
        // A 7-second hole is a drop-out: nothing is invented for it.
        var samples = new List<RideSample> { S(0, 100), S(1, 100), S(2, 100), S(10, 100), S(11, 100) };
        Assert.Equal([0, 1, 2, 10, 11], RideStatisticsCalculator.Resample(samples, 12).Select(x => x.Second).ToList());

        // A 2-second hole is a dropped packet: carried forward.
        var shortGap = new List<RideSample> { S(0, 100), S(1, 120), S(4, 100) };
        var series = RideStatisticsCalculator.Resample(shortGap, 5);
        Assert.Equal([0, 1, 2, 3, 4], series.Select(x => x.Second).ToList());
        Assert.Equal(120, series[3].Power);

        // Nothing is carried past the last real sample, even if the ride ran on.
        Assert.Equal([0, 1], RideStatisticsCalculator.Resample([S(0, 100), S(1, 100)], 30).Select(x => x.Second).ToList());
    }

    [Fact]
    public void Two_hz_and_one_hz_sampling_of_the_same_ride_give_the_same_average()
    {
        // Both describe power 100 + 2k during second k; the 2 Hz version just reports it twice per second.
        var twoHz = Enumerable.Range(0, 120).Select(i => S(i * 0.5, 100 + 2 * (i / 2))).ToList();
        var oneHz = Enumerable.Range(0, 60).Select(i => S(i, 100 + 2 * i)).ToList();
        var a = RideStatisticsCalculator.Compute(twoHz, RideFixtures.Start, RideFixtures.Start.AddSeconds(60));
        var b = RideStatisticsCalculator.Compute(oneHz, RideFixtures.Start, RideFixtures.Start.AddSeconds(60));
        Assert.Equal(a.AveragePowerWatts!.Value, b.AveragePowerWatts!.Value, 9);
        Assert.Equal(a.NormalizedPowerWatts!.Value, b.NormalizedPowerWatts!.Value, 9);
    }
}

public class FitTests
{
    [Fact]
    public void Crc_is_crc16_arc()
    {
        // The FIT CRC is CRC-16/ARC (poly 0x8005 reflected, init 0, no final xor); its standard check value for "123456789" is 0xBB3D.
        Assert.Equal(0xBB3D, FitCrc.Compute("123456789"u8));
        Assert.Equal(0, FitCrc.Compute([]));
    }

    [Fact]
    public void Header_and_crcs_are_valid()
    {
        var bytes = new FitEncoder().Encode(RideFixtures.Ride());
        Assert.Equal(14, bytes[0]);
        Assert.Equal(0x20, bytes[1]);
        Assert.Equal(".FIT", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        var f = FitDecoder.Decode(bytes);
        Assert.True(f.HeaderCrcValid);
        Assert.True(f.FileCrcValid);
        Assert.Equal((uint)(bytes.Length - 16), f.DataSize);

        // Corrupting one byte must break the file CRC.
        var bad = (byte[])bytes.Clone();
        bad[40] ^= 0x01;
        Assert.False(FitDecoder.Decode(bad).FileCrcValid);
    }

    [Fact]
    public void Message_structure_and_values_round_trip()
    {
        var ride = RideFixtures.Ride(120);
        var f = FitDecoder.Decode(new FitEncoder().Encode(ride));
        var byType = f.Messages.GroupBy(m => m.GlobalNumber).ToDictionary(g => g.Key, g => g.ToList());

        Assert.Single(byType[MesgFileId]);
        Assert.Equal(FileTypeActivity, byType[MesgFileId][0].Int(FileIdType));
        Assert.Equal(ManufacturerDevelopment, byType[MesgFileId][0].Int(FileIdManufacturer));
        Assert.Equal("Kinetic Trainer", byType[MesgFileId][0].Text(FileIdProductName));
        Assert.Equal(ToFitTime(ride.StartedAt), (uint)byType[MesgFileId][0].Int(FileIdTimeCreated)!.Value);

        Assert.Equal(2, byType[MesgEvent].Count);
        Assert.Equal(EventTypeStart, byType[MesgEvent][0].Int(EventEventType));
        Assert.Equal(EventTypeStopAll, byType[MesgEvent][1].Int(EventEventType));

        var records = byType[MesgRecord];
        Assert.Equal(240, records.Count);
        Assert.Equal(100, records[0].Int(RecordPower));
        Assert.Equal(300, records[^1].Int(RecordPower));
        Assert.Equal(90, records[0].Int(RecordCadence));
        Assert.Equal(8000, records[0].Int(RecordSpeed));
        Assert.Equal(0, records[0].Int(RecordDistance));
        Assert.Equal((long)Math.Round(ride.Summary.DistanceMeters * 100), records[^1].Int(RecordDistance));
        Assert.True(records.Zip(records.Skip(1)).All(p => p.First.Int(FieldTimestamp) <= p.Second.Int(FieldTimestamp)));

        var lap = Assert.Single(byType[MesgLap]);
        var session = Assert.Single(byType[MesgSession]);
        // Lap and session use different field numbers for cadence; both must decode to the same statistics.
        Assert.Equal(session.Int(SessionAvgCadence), lap.Int(LapAvgCadence));
        Assert.Equal(session.Int(SessionMaxCadence), lap.Int(LapMaxCadence));
        Assert.Equal(90, lap.Int(LapAvgCadence));
        // 2 Hz samples: exactly two records per whole second, and the last record is stamped before the stop event.
        Assert.Equal(2, records.Count(r => r.Int(FieldTimestamp) == records[0].Int(FieldTimestamp)));
        Assert.True(records[^1].Int(FieldTimestamp) < byType[MesgEvent][1].Int(FieldTimestamp));
        Assert.Equal(120_000, session.Int(SessionTotalElapsedTime));
        Assert.Equal(SportCycling, session.Int(SessionSport));
        Assert.Equal(SubSportIndoorCycling, session.Int(SessionSubSport));
        Assert.Equal((long)Math.Round(ride.Summary.AveragePowerWatts!.Value), session.Int(SessionAvgPower));
        Assert.Equal(300, session.Int(SessionMaxPower));
        Assert.Equal((long)Math.Round(ride.Summary.NormalizedPowerWatts!.Value), session.Int(SessionNormalizedPower));
        Assert.Equal(1, session.Int(SessionNumLaps));
        Assert.Equal(lap.Int(LapTotalDistance), session.Int(SessionTotalDistance));
        Assert.Equal(8000, session.Int(SessionMaxSpeed));

        var activity = Assert.Single(byType[MesgActivity]);
        Assert.Equal(1, activity.Int(ActivityNumSessions));
        Assert.Equal(EventActivity, activity.Int(ActivityEvent));
        Assert.Equal(120_000, activity.Int(ActivityTotalTimerTime));

        // Order: file_id first, activity last.
        Assert.Equal(MesgFileId, f.Messages[0].GlobalNumber);
        Assert.Equal(MesgActivity, f.Messages[^1].GlobalNumber);
    }

    [Fact]
    public void Missing_values_are_written_as_invalid_and_decode_as_absent()
    {
        var hub = new TelemetryHub();
        using var recorder = new RideRecorder(hub);
        recorder.Start(RideFixtures.Start);
        recorder.Observe(new TrainerTelemetry { Timestamp = RideFixtures.Start, ConnectionState = TrainerConnectionState.Connected, IsDataValid = true, PowerWatts = 50 });
        var ride = recorder.Stop(RideFixtures.Start.AddSeconds(1))!;
        var f = FitDecoder.Decode(new FitEncoder().Encode(ride));
        var record = Assert.Single(f.Messages, m => m.GlobalNumber == MesgRecord);
        Assert.Equal(50, record.Int(RecordPower));
        Assert.Null(record.Int(RecordCadence));
        Assert.Null(record.Int(RecordSpeed));
        var session = Assert.Single(f.Messages, m => m.GlobalNumber == MesgSession);
        Assert.Null(session.Int(SessionNormalizedPower)); // < 30 s
        Assert.Null(session.Int(SessionAvgCadence));
    }
}

public class TcxTests
{
    [Fact]
    public void Produces_schema_shaped_tcx_with_power_and_cadence()
    {
        var ride = RideFixtures.Ride(60);
        var xml = TcxWriter.Write(ride);
        var doc = XDocument.Parse(xml);
        var tcx = TcxWriter.Tcx;
        var ext = TcxWriter.Ext;
        var activity = doc.Root!.Element(tcx + "Activities")!.Element(tcx + "Activity")!;
        Assert.Equal("Biking", activity.Attribute("Sport")!.Value);
        Assert.Equal("2026-09-16T22:00:00.000Z", activity.Element(tcx + "Id")!.Value);
        var lap = activity.Element(tcx + "Lap")!;
        Assert.Equal("60", lap.Element(tcx + "TotalTimeSeconds")!.Value);
        // Schema order inside Lap
        var names = lap.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(["TotalTimeSeconds", "DistanceMeters", "MaximumSpeed", "Calories", "Intensity", "Cadence", "TriggerMethod", "Track", "Extensions"], names);
        var points = lap.Element(tcx + "Track")!.Elements(tcx + "Trackpoint").ToList();
        Assert.Equal(120, points.Count);
        Assert.Equal(["Time", "DistanceMeters", "Cadence", "Extensions"], points[0].Elements().Select(e => e.Name.LocalName).ToList());
        Assert.Equal("100", points[0].Element(tcx + "Extensions")!.Element(ext + "TPX")!.Element(ext + "Watts")!.Value);
        Assert.Equal("8", points[0].Element(tcx + "Extensions")!.Element(ext + "TPX")!.Element(ext + "Speed")!.Value);
        var lx = lap.Element(tcx + "Extensions")!.Element(ext + "LX")!;
        Assert.Equal("300", lx.Element(ext + "MaxWatts")!.Value);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
    }
}

public class ExporterTests
{
    [Fact]
    public async Task Writes_fit_and_tcx_with_timestamped_names()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kt-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ride = RideFixtures.Ride(40, simulated: true);
            var result = await RideExporter.ExportAsync(ride, folder);
            Assert.True(File.Exists(result.FitPath));
            Assert.True(File.Exists(result.TcxPath));
            Assert.EndsWith("-simulated.fit", result.FitPath);
            Assert.StartsWith("ride-2026", Path.GetFileName(result.FitPath));
            Assert.True(FitDecoder.Decode(await File.ReadAllBytesAsync(result.FitPath)).FileCrcValid);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
