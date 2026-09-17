// Usage: dotnet run --project tools/FitSample -- <output-folder> [seconds]
// Writes ride-sample.fit / ride-sample.tcx: a synthetic 2 Hz ride with a 30 s gap in the middle and a power ramp,
// so external validators (Garmin FIT SDK tools, fitdecode, GoldenCheetah, Strava) can be pointed at real output.
using KineticTrainer.Core.Telemetry;
using KineticTrainer.Recording;
using KineticTrainer.Recording.Fit;
using KineticTrainer.Recording.Tcx;

var folder = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var seconds = args.Length > 1 ? int.Parse(args[1]) : 600;
Directory.CreateDirectory(folder);

var hub = new TelemetryHub();
using var recorder = new RideRecorder(hub);
var start = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
recorder.Start(start);
var rng = new Random(1);
for (var i = 0; i < seconds * 2; i++)
{
    var t = start.AddMilliseconds(500 * i);
    var elapsed = i / 2.0;
    if (elapsed is >= 200 and < 230)
    {
        continue; // sensor drop-out: no samples for 30 s
    }
    var phase = elapsed / seconds;
    var power = phase < 0.2 ? 120 + 200 * phase * 5 : phase < 0.8 ? 220 + 40 * Math.Sin(elapsed / 15.0) : 150;
    var speed = 5.0 + power / 40.0;
    recorder.Observe(new TrainerTelemetry
    {
        Timestamp = t, ConnectionState = TrainerConnectionState.Connected, IsDataValid = true,
        PowerWatts = (int)Math.Round(power + rng.NextDouble() * 6 - 3), CadenceRpm = 88 + rng.NextDouble() * 4 - 2,
        SpeedMetersPerSecond = speed, SensorName = "inRide 48343",
    });
}
var ride = recorder.Stop(start.AddSeconds(seconds))!;
var fitPath = Path.Combine(folder, "ride-sample.fit");
var tcxPath = Path.Combine(folder, "ride-sample.tcx");
File.WriteAllBytes(fitPath, new FitEncoder().Encode(ride));
File.WriteAllText(tcxPath, TcxWriter.Write(ride));
var s = ride.Summary;
Console.WriteLine($"wrote {fitPath} ({new FileInfo(fitPath).Length} bytes) and {tcxPath}");
Console.WriteLine($"samples={s.SampleCount} elapsed={s.ElapsedSeconds}s moving={s.MovingSeconds}s distance={s.DistanceMeters:0.0}m " +
                  $"avgP={s.AveragePowerWatts:0.0} maxP={s.MaxPowerWatts} NP={s.NormalizedPowerWatts:0.0} avgCad={s.AverageCadenceRpm:0.0} " +
                  $"avgSpeed={s.AverageSpeedMetersPerSecond:0.000} maxSpeed={s.MaxSpeedMetersPerSecond:0.000} work={s.WorkKilojoules:0.0}kJ");
