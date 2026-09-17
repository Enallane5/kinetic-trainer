using KineticTrainer.Core.Telemetry;
using Microsoft.UI.Dispatching;

namespace KineticTrainer.App.ViewModels;

/// <summary>
/// Pumps telemetry from a broadcast subscription onto the UI thread, coalescing to the newest sample so the
/// UI never builds a backlog and the publisher is never waited on.
/// </summary>
public sealed class TelemetryUiFeed : IDisposable
{
    private readonly ITelemetryStream _stream;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<TrainerTelemetry> _onSample;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private TrainerTelemetry? _pending;
    private int _queued;

    public TelemetryUiFeed(ITelemetryStream stream, DispatcherQueue dispatcher, string name, Action<TrainerTelemetry> onSample)
    {
        _stream = stream;
        _dispatcher = dispatcher;
        _name = name;
        _onSample = onSample;
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            using var sub = _stream.Subscribe(_name, capacity: 8);
            try
            {
                if (_stream.Latest is { } latest)
                {
                    Post(latest);
                }
                while (await sub.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    TrainerTelemetry? newest = null;
                    while (sub.Reader.TryRead(out var s))
                    {
                        newest = s;
                    }
                    if (newest is not null)
                    {
                        Post(newest);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void Post(TrainerTelemetry sample)
    {
        Volatile.Write(ref _pending, sample);
        if (Interlocked.Exchange(ref _queued, 1) == 0)
        {
            _dispatcher.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _queued, 0);
                var s = Volatile.Read(ref _pending);
                if (s is not null)
                {
                    _onSample(s);
                }
            });
        }
    }

    public void Dispose() => _cts.Cancel();
}
