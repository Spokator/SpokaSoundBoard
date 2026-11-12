using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>A sound decoded once: interleaved stereo float at the mixer rate.</summary>
public sealed class CachedSound(string path, float[] samples, DateTime stamp, long size)
{
    public string Path { get; } = path;
    public float[] Samples { get; } = samples;
    public int Frames => Samples.Length / 2;
    public long Bytes => Samples.LongLength * sizeof(float);
    internal DateTime Stamp { get; } = stamp;
    internal long Size { get; } = size;
    internal long LastUsed;
}

/// <summary>
/// Soundboard sounds kept decoded in memory: a pad starts on the very next audio block, without opening or reading
/// its file, and the audio thread never waits for the disk. Long sounds (above <see cref="MaxSeconds"/>) are not
/// cached and are streamed through <see cref="DecodeAhead"/> instead. The least recently played sounds are dropped
/// when the memory budget is exceeded. Thread-safe.
/// </summary>
public sealed class SoundCache
{
    public const double DefaultMaxSeconds = 30;
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, CachedSound> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, long Size)> _tooLong = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes, _clock;

    public SoundCache(int sampleRate, double maxSeconds = DefaultMaxSeconds, long maxBytes = DefaultMaxBytes)
    {
        SampleRate = sampleRate;
        MaxSeconds = maxSeconds;
        MaxBytes = maxBytes;
    }

    public int SampleRate { get; }
    public double MaxSeconds { get; }
    public long MaxBytes { get; }

    public int Count
    {
        get { lock (_lock) return _sounds.Count; }
    }

    public long Bytes
    {
        get { lock (_lock) return _bytes; }
    }

    /// <summary>The decoded sound if it is in memory (no disk access: safe on the UI thread).</summary>
    public CachedSound? TryGet(string path)
    {
        lock (_lock)
        {
            if (!_sounds.TryGetValue(path, out var sound)) return null;
            sound.LastUsed = ++_clock;
            return sound;
        }
    }

    /// <summary>Returns the sound, decoding it now if needed (blocking I/O: worker threads only). Null when the
    /// file is too long to be cached or cannot be read.</summary>
    public CachedSound? GetOrLoad(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists) return null;
        }
        catch
        {
            return null;
        }
        var stamp = file.LastWriteTimeUtc;
        var size = file.Length;

        lock (_lock)
        {
            if (_sounds.TryGetValue(path, out var known))
            {
                if (known.Stamp == stamp && known.Size == size)
                {
                    known.LastUsed = ++_clock;
                    return known;
                }
                RemoveUnlocked(path); // the file changed on disk
            }
            if (_tooLong.TryGetValue(path, out var skipped) && skipped == (stamp, size)) return null;
        }

        float[]? samples;
        try
        {
            samples = Decode(path);
        }
        catch
        {
            return null;
        }

        lock (_lock)
        {
            if (samples == null)
            {
                _tooLong[path] = (stamp, size);
                return null;
            }
            if (_sounds.TryGetValue(path, out var raced)) return raced;
            var sound = new CachedSound(path, samples, stamp, size) { LastUsed = ++_clock };
            _sounds[path] = sound;
            _bytes += sound.Bytes;
            TrimUnlocked(sound);
            return sound;
        }
    }

    /// <summary>Decodes the given sounds one after the other in the background (already cached ones are skipped).</summary>
    public Task PreloadAsync(IEnumerable<string> paths, CancellationToken cancellation = default)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Task.Run(() =>
        {
            foreach (var path in list)
            {
                if (cancellation.IsCancellationRequested) return;
                GetOrLoad(path);
            }
        }, cancellation);
    }

    /// <summary>Forgets every sound whose path is not in <paramref name="keep"/> (removed pads).</summary>
    public void Retain(IEnumerable<string> keep)
    {
        var set = new HashSet<string>(keep.Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var path in _sounds.Keys.Where(p => !set.Contains(p)).ToList()) RemoveUnlocked(path);
            foreach (var path in _tooLong.Keys.Where(p => !set.Contains(p)).ToList()) _tooLong.Remove(path);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _sounds.Clear();
            _tooLong.Clear();
            _bytes = 0;
        }
    }

    private float[]? Decode(string path)
    {
        var (reader, provider) = AudioFileLoader.Open(path, SampleRate);
        using (reader)
        {
            var maxFrames = (long)(MaxSeconds * SampleRate);
            if (reader.TotalTime.TotalSeconds > MaxSeconds + 0.5) return null;

            var blocks = new List<float[]>();
            var block = new float[SampleRate * 2]; // one second
            var filled = 0;
            long total = 0;
            while (true)
            {
                var n = provider.Read(block, filled, block.Length - filled);
                if (n <= 0) break;
                filled += n;
                total += n;
                if (total / 2 > maxFrames) return null; // the header lied about the length
                if (filled == block.Length)
                {
                    blocks.Add(block);
                    block = new float[block.Length];
                    filled = 0;
                }
            }

            var samples = new float[total];
            var offset = 0;
            foreach (var b in blocks)
            {
                Array.Copy(b, 0, samples, offset, b.Length);
                offset += b.Length;
            }
            Array.Copy(block, 0, samples, offset, filled);
            return samples;
        }
    }

    private void TrimUnlocked(CachedSound keep)
    {
        while (_bytes > MaxBytes && _sounds.Count > 1)
        {
            CachedSound? oldest = null;
            foreach (var s in _sounds.Values)
                if (!ReferenceEquals(s, keep) && (oldest == null || s.LastUsed < oldest.LastUsed)) oldest = s;
            if (oldest == null) return;
            RemoveUnlocked(oldest.Path);
        }
    }

    private void RemoveUnlocked(string path)
    {
        if (_sounds.Remove(path, out var sound)) _bytes -= sound.Bytes;
    }
}

/// <summary>Plays a <see cref="CachedSound"/> (or a region of it, optionally looped). Allocation-free.</summary>
public sealed class CachedSoundSource : ISampleProvider
{
    private readonly float[] _samples;
    private readonly int _start, _end; // in samples (interleaved)
    private readonly bool _loop;
    private int _position;

    public CachedSoundSource(CachedSound sound, int sampleRate, double startSeconds = 0, double endSeconds = 0, bool loop = false)
    {
        _samples = sound.Samples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        var start = (int)Math.Clamp(Math.Round(Math.Max(0, startSeconds) * sampleRate), 0, sound.Frames) * 2;
        var end = endSeconds > startSeconds && endSeconds > 0
            ? (int)Math.Clamp(Math.Round(endSeconds * sampleRate), 0, sound.Frames) * 2
            : _samples.Length;
        if (start >= end) start = 0; // start beyond the end of the file: play it from the beginning
        _start = start;
        _end = Math.Max(start, end);
        _loop = loop;
        _position = _start;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var done = 0;
        while (done < count)
        {
            var available = _end - _position;
            if (available <= 0)
            {
                if (!_loop || _end == _start) break;
                _position = _start;
                continue;
            }
            var n = Math.Min(available, count - done);
            Array.Copy(_samples, _position, buffer, offset + done, n);
            _position += n;
            done += n;
        }
        return done;
    }
}
