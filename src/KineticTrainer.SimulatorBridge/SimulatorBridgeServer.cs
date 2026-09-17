using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Core.Telemetry;

namespace KineticTrainer.SimulatorBridge;

/// <summary>One line of the NDJSON stream. Field names are the wire contract for the Unity side.</summary>
public sealed record BridgeMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = "telemetry";
    [JsonPropertyName("seq")] public long Seq { get; init; }
    /// <summary>Unix time in milliseconds.</summary>
    [JsonPropertyName("timestamp")] public long Timestamp { get; init; }
    [JsonPropertyName("connection")] public string Connection { get; init; } = "";
    [JsonPropertyName("valid")] public bool Valid { get; init; }
    [JsonPropertyName("power")] public int? Power { get; init; }
    [JsonPropertyName("cadence")] public double? Cadence { get; init; }
    [JsonPropertyName("speedMps")] public double? SpeedMps { get; init; }
    [JsonPropertyName("rollerRpm")] public double? RollerRpm { get; init; }
    [JsonPropertyName("coasting")] public bool? Coasting { get; init; }
    [JsonPropertyName("simulated")] public bool Simulated { get; init; }

    public static BridgeMessage From(TrainerTelemetry t) => new()
    {
        Seq = t.Sequence,
        Timestamp = t.Timestamp.ToUnixTimeMilliseconds(),
        Connection = t.ConnectionState.ToString().ToLowerInvariant(),
        Valid = t.IsDataValid,
        Power = t.PowerWatts,
        Cadence = t.CadenceRpm is { } c ? Math.Round(c, 1) : null,
        SpeedMps = t.SpeedMetersPerSecond is { } s ? Math.Round(s, 3) : null,
        RollerRpm = t.RollerRpm is { } r ? Math.Round(r, 1) : null,
        Coasting = t.Coasting,
        Simulated = t.IsSimulated,
    };
}

/// <summary>
/// Localhost-only telemetry server. Binds 127.0.0.1 exclusively, accepts any number of clients, and writes every
/// telemetry sample as one JSON line. Each client has its own bounded queue so a stalled reader is dropped, never
/// waited on. Nothing in here can throw into the sensor pipeline: it is a subscriber like any other.
/// </summary>
public sealed class SimulatorBridgeServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ITelemetryStream _telemetry;
    private readonly IDiagnosticLog _log;
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _clients;
    private long _linesSent;

    public SimulatorBridgeServer(ITelemetryStream telemetry, IDiagnosticLog log)
    {
        _telemetry = telemetry;
        _log = log;
    }

    public bool IsRunning => _listener is not null;

    public int Port { get; private set; }

    public int ClientCount => Volatile.Read(ref _clients);

    public long LinesSent => Interlocked.Read(ref _linesSent);

    public string? LastError { get; private set; }

    public event Action? StatusChanged;

    /// <summary>Starts listening on 127.0.0.1:<paramref name="port"/>. Returns false (and logs) if the port is unavailable.</summary>
    public bool Start(int port)
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                if (Port == port)
                {
                    return true;
                }
                StopCore();
            }
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.ExclusiveAddressUse = true;
                listener.Start(backlog: 8);
                _listener = listener;
                Port = port;
                LastError = null;
                _cts = new CancellationTokenSource();
                _acceptTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
                _log.Info(DiagnosticCategory.Bridge, $"Simulator bridge listening on 127.0.0.1:{port} (loopback only)");
                StatusChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log.Error(DiagnosticCategory.Bridge, $"Simulator bridge could not listen on 127.0.0.1:{port}: {ex.Message}");
                StatusChanged?.Invoke();
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
        StatusChanged?.Invoke();
    }

    private void StopCore()
    {
        if (_listener is null)
        {
            return;
        }
        _cts?.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignored
        }
        _listener = null;
        _log.Info(DiagnosticCategory.Bridge, "Simulator bridge stopped");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                _log.Warn(DiagnosticCategory.Bridge, $"Accept failed: {ex.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            // Defence in depth: the listener is bound to loopback, but refuse anything else anyway.
            if (client.Client.RemoteEndPoint is not IPEndPoint { Address: var addr } || !IPAddress.IsLoopback(addr))
            {
                _log.Warn(DiagnosticCategory.Bridge, $"Rejected non-loopback client {client.Client.RemoteEndPoint}");
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => ServeClientAsync(client, ct), ct);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Interlocked.Increment(ref _clients);
        StatusChanged?.Invoke();
        _log.Info(DiagnosticCategory.Bridge, $"Simulator client connected: {endpoint}");
        using var subscription = _telemetry.Subscribe($"bridge:{endpoint}", capacity: 32);
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            var hello = JsonSerializer.Serialize(new { type = "hello", app = "KineticTrainer", protocol = 1, units = "SI" }, JsonOptions) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(hello), ct).ConfigureAwait(false);

            if (_telemetry.Latest is { } latest)
            {
                await WriteLineAsync(stream, latest, ct).ConfigureAwait(false);
            }

            while (await subscription.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (subscription.Reader.TryRead(out var sample))
                {
                    await WriteLineAsync(stream, sample, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _log.Info(DiagnosticCategory.Bridge, $"Simulator client {endpoint} disconnected ({ex.GetType().Name})");
        }
        catch (Exception ex)
        {
            _log.Warn(DiagnosticCategory.Bridge, $"Simulator client {endpoint} error: {ex.Message}");
        }
        finally
        {
            client.Dispose();
            Interlocked.Decrement(ref _clients);
            StatusChanged?.Invoke();
        }
    }

    private async Task WriteLineAsync(NetworkStream stream, TrainerTelemetry sample, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(BridgeMessage.From(sample), JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2)); // a client that stops reading is cut off, not waited for
        await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        Interlocked.Increment(ref _linesSent);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
        _cts?.Dispose();
    }
}
