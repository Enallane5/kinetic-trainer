using KineticTrainer.Core.Diagnostics;
using KineticTrainer.Workouts.Playback;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace KineticTrainer.App.Services;

/// <summary>
/// Workout audio, fully offline: short tones are synthesised in memory (no sound files) and spoken cues use the
/// Windows speech synthesiser with a locally installed voice. Every failure (no audio device, no voice) is logged
/// once and otherwise ignored, so audio can never disturb a ride.
/// </summary>
public sealed class CuePlayer : IDisposable
{
    private const int SampleRate = 44100;

    private readonly DiagnosticLog _log;
    private readonly Lazy<MediaPlayer?> _tonePlayer;
    private readonly Lazy<MediaPlayer?> _voicePlayer;
    private readonly Lazy<SpeechSynthesizer?> _synth;
    private readonly byte[] _tick;
    private readonly byte[] _go;
    private readonly byte[] _done;
    private bool _warned;

    public CuePlayer(DiagnosticLog log)
    {
        _log = log;
        _tick = Wav([(880, 0.12)], 0.35);
        _go = Wav([(1320, 0.30)], 0.40);
        _done = Wav([(880, 0.15), (1100, 0.15), (1320, 0.35)], 0.40);
        _tonePlayer = new(() => Create(() => new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Alerts }));
        _voicePlayer = new(() => Create(() => new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech }));
        _synth = new(() => Create(() => new SpeechSynthesizer()));
    }

    public bool TonesEnabled { get; set; } = true;

    public bool SpeechEnabled { get; set; } = true;

    public void Play(WorkoutCue cue)
    {
        switch (cue.Kind)
        {
            case CueKind.Countdown:
                _ = ToneAsync(_tick);
                break;
            case CueKind.SegmentStart:
            case CueKind.Recovery:
            case CueKind.Cooldown:
                _ = ToneAsync(_go);
                _ = SayAsync(cue.Message);
                break;
            case CueKind.Complete:
                _ = ToneAsync(_done);
                _ = SayAsync(cue.Message);
                break;
            case CueKind.Upcoming:
            case CueKind.Text:
                _ = SayAsync(cue.Message);
                break;
        }
    }

    public void Test()
    {
        var tones = TonesEnabled;
        var speech = SpeechEnabled;
        TonesEnabled = SpeechEnabled = true;
        _ = ToneAsync(_go);
        _ = SayAsync("Audio cues are working. Next: Interval 1 of 5, 250 W for 3 minutes.");
        TonesEnabled = tones;
        SpeechEnabled = speech;
    }

    private async Task ToneAsync(byte[] wav)
    {
        if (!TonesEnabled || _tonePlayer.Value is not { } player)
        {
            return;
        }
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(wav);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
            player.Play();
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private async Task SayAsync(string text)
    {
        if (!SpeechEnabled || string.IsNullOrWhiteSpace(text) || _synth.Value is not { } synth || _voicePlayer.Value is not { } player)
        {
            return;
        }
        try
        {
            // The screen shows "240 W"; the voice says "240 watts".
            var spoken = CueSpeech.ToSpoken(text);
            if (spoken.Length == 0)
            {
                return;
            }
            var stream = await synth.SynthesizeTextToStreamAsync(spoken);
            player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            player.Play();
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private T? Create<T>(Func<T> factory) where T : class
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            Warn(ex);
            return null;
        }
    }

    private void Warn(Exception ex)
    {
        if (!_warned)
        {
            _warned = true;
            _log.Warn(DiagnosticCategory.App, $"Audio cues unavailable: {ex.Message}");
        }
    }

    /// <summary>16-bit mono PCM WAV of consecutive sine tones with short fades (no clicks).</summary>
    internal static byte[] Wav(IReadOnlyList<(double Hz, double Seconds)> tones, double volume)
    {
        var samples = new List<short>();
        foreach (var (hz, seconds) in tones)
        {
            var n = (int)(SampleRate * seconds);
            var fade = Math.Min(n / 4, SampleRate / 100);
            for (var i = 0; i < n; i++)
            {
                var env = i < fade ? (double)i / fade : i > n - fade ? (double)(n - i) / fade : 1.0;
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * env * volume * short.MaxValue));
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            w.Write(s);
        }
        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_tonePlayer.IsValueCreated)
        {
            _tonePlayer.Value?.Dispose();
        }
        if (_voicePlayer.IsValueCreated)
        {
            _voicePlayer.Value?.Dispose();
        }
        if (_synth.IsValueCreated)
        {
            _synth.Value?.Dispose();
        }
    }
}
