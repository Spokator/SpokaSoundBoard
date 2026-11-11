using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Mixes the soundboard sounds that are playing at the same time. The audio thread never disposes a file reader:
/// finished sounds are handed to the thread pool (like <see cref="MusicPlayer"/> does with its decks), and the
/// number of simultaneous sounds is capped so that hammering a hotkey cannot overload the audio thread.
/// </summary>
public sealed class SoundPadPlayer : ISampleProvider, IDisposable
{
    /// <summary>Sounds playing at once; starting one more stops the oldest.</summary>
    public const int MaxVoices = 24;

    private readonly object _lock = new();
    private readonly List<Voice> _voices = new(MaxVoices + 1);

    // Readers waiting to be disposed off the audio thread. Preallocated: adding never allocates in steady state.
    private readonly WaveStream?[] _graveyard = new WaveStream?[MaxVoices * 4];
    private int _graveyardCount;
    private int _drainQueued;
    private readonly Drainer _drainer;

    private float[] _scratch = new float[4096];

    public SoundPadPlayer(int sampleRate)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _drainer = new Drainer(this);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Starts a sound. Opening the file happens on a worker thread.</summary>
    /// <param name="startSeconds">Start position inside the file.</param>
    /// <param name="endSeconds">End position inside the file (0 or less = until the end).</param>
    public Task PlayAsync(Guid id, string path, float volume, bool loop, double startSeconds = 0, double endSeconds = 0) => Task.Run(() =>
    {
        var (reader, provider) = AudioFileLoader.Open(path, WaveFormat.SampleRate);
        var start = TimeSpan.FromSeconds(Math.Max(0, startSeconds));
        if (start > TimeSpan.Zero && start < reader.TotalTime) reader.CurrentTime = start;
        else start = TimeSpan.Zero;

        long budget = -1;
        if (endSeconds > startSeconds && endSeconds > 0)
            budget = (long)((endSeconds - start.TotalSeconds) * WaveFormat.SampleRate) * 2;

        WaveStream? evicted = null;
        lock (_lock)
        {
            if (_voices.Count >= MaxVoices)
            {
                evicted = _voices[0].Reader;
                _voices.RemoveAt(0);
            }
            _voices.Add(new Voice(id, path, reader, provider, volume, loop, start, budget));
        }
        evicted?.Dispose();
    });

    /// <summary>Number of sounds currently playing.</summary>
    public int ActiveCount
    {
        get { lock (_lock) return _voices.Count; }
    }

    public void Stop(Guid id)
    {
        List<WaveStream> stopped = [];
        lock (_lock)
        {
            for (var i = _voices.Count - 1; i >= 0; i--)
            {
                if (_voices[i].Id != id) continue;
                stopped.Add(_voices[i].Reader);
                _voices.RemoveAt(i);
            }
        }
        foreach (var reader in stopped) reader.Dispose(); // outside the lock the audio thread needs
    }

    public void StopAll()
    {
        List<WaveStream> stopped;
        lock (_lock)
        {
            stopped = _voices.Select(v => v.Reader).ToList();
            _voices.Clear();
        }
        foreach (var reader in stopped) reader.Dispose();
    }

    public bool IsPlaying(Guid id)
    {
        lock (_lock) return _voices.Any(v => v.Id == id);
    }

    public HashSet<Guid> PlayingIds()
    {
        lock (_lock) return _voices.Select(v => v.Id).ToHashSet();
    }

    public void SetVolume(Guid id, float volume)
    {
        lock (_lock)
            foreach (var v in _voices.Where(v => v.Id == id))
                v.Volume = volume;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        if (_scratch.Length < count) _scratch = new float[count];

        var retired = false;
        lock (_lock)
        {
            for (var i = _voices.Count - 1; i >= 0; i--)
            {
                var v = _voices[i];
                var total = 0;
                try
                {
                    while (total < count)
                    {
                        var want = count - total;
                        if (v.Remaining >= 0) want = (int)Math.Min(want, v.Remaining);
                        var n = want > 0 ? v.Provider.Read(_scratch, total, want) : 0;
                        if (n <= 0)
                        {
                            if (!v.Loop || total == 0 && v.LoopedEmpty) break;
                            v.LoopedEmpty = total == 0;
                            v.Reader.CurrentTime = v.Start;
                            v.Remaining = v.Budget;
                            continue;
                        }
                        v.LoopedEmpty = false;
                        if (v.Remaining >= 0) v.Remaining -= n;
                        total += n;
                    }
                }
                catch
                {
                    total = 0;
                }

                var volume = v.Volume;
                for (var s = 0; s < total; s++) buffer[offset + s] += _scratch[s] * volume;

                if (total < count)
                {
                    _voices.RemoveAt(i);
                    retired |= Retire(v.Reader);
                }
            }
        }
        if (retired && Interlocked.Exchange(ref _drainQueued, 1) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(_drainer, preferLocal: false);
        return count;
    }

    public void Dispose()
    {
        StopAll();
        DrainGraveyard();
    }

    /// <summary>Queues a reader for disposal on the thread pool. Called with <see cref="_lock"/> held.</summary>
    private bool Retire(WaveStream reader)
    {
        if (_graveyardCount < _graveyard.Length)
        {
            _graveyard[_graveyardCount++] = reader;
            return true;
        }
        // Only if the thread pool is far behind: dispose here rather than leak the file handle.
        try { reader.Dispose(); } catch { /* never throw on the audio thread */ }
        return false;
    }

    private void DrainGraveyard()
    {
        Volatile.Write(ref _drainQueued, 0);
        while (true)
        {
            WaveStream? reader;
            lock (_lock)
            {
                if (_graveyardCount == 0) return;
                reader = _graveyard[--_graveyardCount];
                _graveyard[_graveyardCount] = null;
            }
            try { reader?.Dispose(); } catch { /* best effort */ }
        }
    }

    private sealed class Drainer(SoundPadPlayer owner) : IThreadPoolWorkItem
    {
        public void Execute() => owner.DrainGraveyard();
    }

    private sealed class Voice(Guid id, string path, WaveStream reader, ISampleProvider provider, float volume, bool loop, TimeSpan start, long budget)
    {
        public TimeSpan Start { get; } = start;

        /// <summary>Samples to play per pass (-1 = until the end of the file).</summary>
        public long Budget { get; } = budget;

        public long Remaining { get; set; } = budget;

        public Guid Id { get; } = id;
        public string Path { get; } = path;
        public WaveStream Reader { get; } = reader;
        public ISampleProvider Provider { get; } = provider;
        public float Volume { get; set; } = volume;
        public bool Loop { get; } = loop;
        public bool LoopedEmpty { get; set; }
    }
}
