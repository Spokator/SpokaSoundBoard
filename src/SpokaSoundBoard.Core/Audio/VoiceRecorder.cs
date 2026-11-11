using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Records the microphone into a new soundboard sound. The audio thread only copies samples into a preallocated ring
/// buffer; a worker collects them, and <see cref="StopAsync"/> trims the silence at both ends and writes a WAV file.
/// </summary>
public sealed class VoiceRecorder
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(2);

    private readonly int _sampleRate;
    private readonly float[] _ring;
    private readonly int _mask;
    private long _written; // advanced by the audio thread
    private long _read;    // advanced by the worker
    private readonly float[] _recording;
    private int _count;
    private volatile bool _stopping;
    private readonly Task _worker;

    public VoiceRecorder(int sampleRate)
    {
        _sampleRate = sampleRate;
        _ring = new float[1 << 18]; // about 5 s of slack at 48 kHz
        _mask = _ring.Length - 1;
        _recording = new float[(int)(sampleRate * MaxDuration.TotalSeconds)];
        _worker = Task.Run(CollectAsync);
    }

    /// <summary>Length recorded so far.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(Volatile.Read(ref _count) / (double)_sampleRate);

    /// <summary>The maximum length is reached: the caller should stop.</summary>
    public bool IsFull => Volatile.Read(ref _count) >= _recording.Length;

    /// <summary>Peak of the last samples collected, 0–1, for a level meter.</summary>
    public float RecentPeak { get; private set; }

    /// <summary>Audio thread: copies mono samples, never blocks nor allocates. Drops samples if the worker is far behind.</summary>
    public void Write(ReadOnlySpan<float> mono)
    {
        if (_stopping) return;
        var w = _written;
        var free = _ring.Length - (int)(w - Volatile.Read(ref _read));
        var n = Math.Min(mono.Length, free);
        for (var i = 0; i < n; i++) _ring[(int)((w + i) & _mask)] = mono[i];
        Volatile.Write(ref _written, w + n);
    }

    private async Task CollectAsync()
    {
        while (true)
        {
            var stopping = _stopping;
            var w = Volatile.Read(ref _written);
            var r = _read;
            var peak = 0f;
            for (; r < w; r++)
            {
                var x = _ring[(int)(r & _mask)];
                peak = MathF.Max(peak, MathF.Abs(x));
                if (_count < _recording.Length) _recording[_count++] = x;
            }
            Volatile.Write(ref _read, r);
            RecentPeak = peak;
            if (stopping) return;
            await Task.Delay(40).ConfigureAwait(false);
        }
    }

    /// <summary>Stops, trims the silence and writes <paramref name="path"/>. Returns the kept length (zero: nothing but silence, no file).</summary>
    public async Task<TimeSpan> StopAsync(string path)
    {
        _stopping = true;
        await _worker.ConfigureAwait(false);
        var (start, end) = TrimSilence(_recording, _count, _sampleRate);
        if (end <= start) return TimeSpan.Zero;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 1));
            writer.WriteSamples(_recording, start, end - start);
        }).ConfigureAwait(false);
        return TimeSpan.FromSeconds((end - start) / (double)_sampleRate);
    }

    /// <summary>Abandons the recording.</summary>
    public Task CancelAsync()
    {
        _stopping = true;
        return _worker;
    }

    /// <summary>
    /// Range to keep: from a little before the first sample above the threshold to a little after the last one
    /// (so the clicks of the « record » button and the silence around the phrase disappear).
    /// </summary>
    public static (int Start, int End) TrimSilence(float[] samples, int count, int sampleRate, float threshold = 0.02f)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < count; i++)
        {
            if (MathF.Abs(samples[i]) < threshold) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return (0, 0);
        var margin = sampleRate / 20; // 50 ms
        return (Math.Max(0, first - margin), Math.Min(count, last + margin + 1));
    }
}
