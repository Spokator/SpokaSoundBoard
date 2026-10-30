using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>Mixes any number of simultaneously playing soundboard sounds.</summary>
public sealed class SoundPadPlayer : ISampleProvider, IDisposable
{
    private readonly object _lock = new();
    private readonly List<Voice> _voices = [];
    private float[] _scratch = new float[4096];

    public SoundPadPlayer(int sampleRate) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

    public WaveFormat WaveFormat { get; }

    /// <summary>Starts a sound. Opening the file happens on a worker thread.</summary>
    public Task PlayAsync(Guid id, string path, float volume, bool loop) => Task.Run(() =>
    {
        var (reader, provider) = AudioFileLoader.Open(path, WaveFormat.SampleRate);
        lock (_lock) _voices.Add(new Voice(id, path, reader, provider, volume, loop));
    });

    public void Stop(Guid id)
    {
        lock (_lock)
        {
            foreach (var v in _voices.Where(v => v.Id == id)) v.Reader.Dispose();
            _voices.RemoveAll(v => v.Id == id);
        }
    }

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var v in _voices) v.Reader.Dispose();
            _voices.Clear();
        }
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
                        var n = v.Provider.Read(_scratch, total, count - total);
                        if (n <= 0)
                        {
                            if (!v.Loop || total == 0 && v.LoopedEmpty) break;
                            v.LoopedEmpty = total == 0;
                            v.Reader.Position = 0;
                            continue;
                        }
                        v.LoopedEmpty = false;
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
                    v.Reader.Dispose();
                    _voices.RemoveAt(i);
                }
            }
        }
        return count;
    }

    public void Dispose() => StopAll();

    private sealed class Voice(Guid id, string path, WaveStream reader, ISampleProvider provider, float volume, bool loop)
    {
        public Guid Id { get; } = id;
        public string Path { get; } = path;
        public WaveStream Reader { get; } = reader;
        public ISampleProvider Provider { get; } = provider;
        public float Volume { get; set; } = volume;
        public bool Loop { get; } = loop;
        public bool LoopedEmpty { get; set; }
    }
}
