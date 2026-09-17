using KineticTrainer.Recording.Fit;
using KineticTrainer.Recording.Tcx;

namespace KineticTrainer.Recording;

public sealed record ExportResult(string FitPath, string TcxPath);

/// <summary>Writes a finished ride as a FIT file and a TCX file into a folder.</summary>
public static class RideExporter
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KineticTrainer", "Rides");

    public static string FileStem(RideRecording ride) =>
        $"ride-{ride.StartedAt.ToLocalTime():yyyyMMdd-HHmmss}{(ride.Simulated ? "-simulated" : "")}";

    public static async Task<ExportResult> ExportAsync(RideRecording ride, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var stem = FileStem(ride);
        var fitPath = Path.Combine(folder, stem + ".fit");
        var tcxPath = Path.Combine(folder, stem + ".tcx");
        var fit = new FitEncoder().Encode(ride);
        var tcx = TcxWriter.Write(ride);
        await File.WriteAllBytesAsync(fitPath, fit, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(tcxPath, tcx, ct).ConfigureAwait(false);
        return new ExportResult(fitPath, tcxPath);
    }
}
