using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>Plays one music track at a time. Always returns a full buffer (silence when idle).</summary>
public sealed class MusicPlayer : ISampleProvider, IDisposable
{
    private readonly object _lock = new();
    private WaveStream? _reader;
    private ISampleProvider? _provider;
    private bool _playing;

    public MusicPlayer(int sampleRate) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

    public WaveFormat WaveFormat { get; }

    public string? CurrentFile { get; private set; }

    public bool IsPlaying => _playing;

    public bool HasTrack => _reader != null;

    /// <summary>Raised on the audio thread when a track reaches its end.</summary>
    public event Action? TrackEnded;

    public TimeSpan Position
    {
        get { lock (_lock) return _reader?.CurrentTime ?? TimeSpan.Zero; }
    }

    public TimeSpan Duration
    {
        get { lock (_lock) return _reader?.TotalTime ?? TimeSpan.Zero; }
    }

    public void Load(string path, bool autoPlay = true)
    {
        var (reader, provider) = AudioFileLoader.Open(path, WaveFormat.SampleRate);
        WaveStream? old;
        lock (_lock)
        {
            old = _reader;
            _reader = reader;
            _provider = provider;
            CurrentFile = path;
            _playing = autoPlay;
        }
        old?.Dispose();
    }

    public void Play()
    {
        if (_reader != null) _playing = true;
    }

    public void Pause() => _playing = false;

    public void Stop()
    {
        WaveStream? old;
        lock (_lock)
        {
            _playing = false;
            old = _reader;
            _reader = null;
            _provider = null;
            CurrentFile = null;
        }
        old?.Dispose();
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_reader == null) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > _reader.TotalTime) position = _reader.TotalTime;
            _reader.CurrentTime = position;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var ended = false;
        lock (_lock)
        {
            if (!_playing || _provider == null)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            var total = 0;
            try
            {
                while (total < count)
                {
                    var n = _provider.Read(buffer, offset + total, count - total);
                    if (n <= 0) break;
                    total += n;
                }
            }
            catch
            {
                // Corrupted file: treat as end of track.
            }

            if (total < count)
            {
                Array.Clear(buffer, offset + total, count - total);
                _playing = false;
                ended = true;
            }
        }

        if (ended) TrackEnded?.Invoke();
        return count;
    }

    public void Dispose() => Stop();
}
