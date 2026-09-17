using System.Text;

namespace KineticTrainer.Core.Diagnostics;

/// <summary>
/// Streams diagnostic events to a text file on a background task. Uses its own bounded
/// subscription, so a slow disk can only cause dropped log lines - never a stalled ride.
/// </summary>
public sealed class DiagnosticFileWriter : IAsyncDisposable
{
    private readonly IDiagnosticLog _log;
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _keepFiles;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public DiagnosticFileWriter(IDiagnosticLog log, string directory, long maxBytes = 64 * 1024 * 1024, int keepFiles = 30)
    {
        _log = log;
        _directory = directory;
        _maxBytes = maxBytes;
        _keepFiles = keepFiles;
    }

    public string? CurrentFile { get; private set; }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        var subscription = _log.Subscribe("log-file", 8192);
        _loop = Task.Run(() => RunAsync(subscription, _cts.Token));
    }

    private async Task RunAsync(Streaming.Broadcast<DiagnosticEvent>.Subscription subscription, CancellationToken ct)
    {
        using var _ = subscription;
        try
        {
            Directory.CreateDirectory(_directory);
            PruneOldFiles();
            CurrentFile = Path.Combine(_directory, $"kinetic-trainer-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            await using var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.Read, 16384, useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            long written = 0;
            long lastDropped = 0;
            while (await subscription.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (subscription.Reader.TryRead(out var evt))
                {
                    if (written > _maxBytes)
                    {
                        continue; // size cap reached: keep draining, stop writing
                    }
                    var line = $"{evt.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}\t{evt.Severity}\t{evt.Category}\t{evt.Message}" +
                               (evt.RawHex is null ? string.Empty : "\t" + evt.RawHex);
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    written += line.Length + 2;
                }
                var dropped = subscription.DroppedCount;
                if (dropped != lastDropped)
                {
                    await writer.WriteLineAsync($"# log writer fell behind; {dropped - lastDropped} events not written").ConfigureAwait(false);
                    lastDropped = dropped;
                }
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Logging must never take the app down. Report once through the in-memory log.
            _log.Warn(DiagnosticCategory.App, $"Log file writer stopped: {ex.Message}");
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            var files = new DirectoryInfo(_directory).GetFiles("kinetic-trainer-*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(_keepFiles);
            foreach (var f in files)
            {
                f.Delete();
            }
        }
        catch
        {
            // best effort
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
        _cts.Dispose();
    }
}
