using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// One opened track of the <see cref="MusicPlayer"/> (the player owns two or more decks while crossfading).
/// The envelope fields are owned by the player and only touched under its lock.
/// </summary>
internal sealed class MusicDeck : IDisposable
{
    /// <summary>Seconds of music decoded in advance off the audio thread.</summary>
    public const double BufferSeconds = 2.0;

    /// <summary>Decoded before <see cref="Open"/> returns, so the track can start on the next block.</summary>
    public const double PrefillSeconds = 0.4;

    private readonly WaveStream? _reader;
    private readonly DecodeAhead? _ahead;
    private readonly VarispeedProvider _varispeed;
    private readonly int _sampleRate;
    private int _disposed;
    private bool _failed;

    // Envelopes (owned by MusicPlayer, under its lock).
    /// <summary>Crossfade / load envelope, 0..1 mapped through an equal-power curve.</summary>
    public Ramp Fade;
    /// <summary>Linear micro-fade used around seeks.</summary>
    public Ramp SeekRamp;
    /// <summary>Smoothed loudness-normalization gain and its target.</summary>
    public float Gain = 1f, GainTarget = 1f;
    /// <summary>Seek requested while audible: applied once <see cref="SeekRamp"/> reaches 0.</summary>
    public TimeSpan? PendingSeek;
    /// <summary>A seek was handed to the decoder: <see cref="SeekRamp"/> rises again once its first samples are ready.</summary>
    public bool RampWhenReady;

    /// <summary>A deck that reads a source synchronously (the tests use it with generated sources).</summary>
    internal MusicDeck(string path, WaveStream? reader, ISampleProvider source, TimeSpan duration, int sampleRate)
    {
        Path = path;
        _reader = reader;
        _sampleRate = sampleRate;
        _varispeed = new VarispeedProvider(source);
        Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        DurationSeconds = Duration.TotalSeconds;
        Fade.Set(1f);
        SeekRamp.Set(1f);
    }

    /// <summary>A deck fed by a decoder working ahead on the thread pool (the audio thread only copies samples).</summary>
    internal MusicDeck(string path, DecodeAhead ahead, int sampleRate)
        : this(path, null, ahead, ahead.Duration, sampleRate)
    {
        _ahead = ahead;
    }

    /// <summary>Opens a file and decodes its first moments (blocking I/O: never call this on the audio thread).</summary>
    public static MusicDeck Open(string path, int sampleRate)
    {
        var ahead = DecodeAhead.Open(path, sampleRate, BufferSeconds, PrefillSeconds);
        try
        {
            return new MusicDeck(path, ahead, sampleRate);
        }
        catch
        {
            ahead.Dispose();
            throw;
        }
    }

    /// <summary>True when seeks complete in the background (see <see cref="Waiting"/>).</summary>
    public bool IsAsync => _ahead != null;

    /// <summary>A seek is being decoded: nothing to play yet (the player outputs silence meanwhile).</summary>
    public bool Waiting => _ahead?.IsWaiting ?? false;

    public string Path { get; }

    public TimeSpan Duration { get; }

    public double DurationSeconds { get; }

    /// <summary>Current position in file time (seconds), independent of the playback speed.</summary>
    public double PositionSeconds => Math.Max(0, _varispeed.SourcePosition / _sampleRate);

    public bool Ended => _failed || _varispeed.IsEnded;

    public Exception? Error => _varispeed.Error ?? _ahead?.Error;

    public void SetGain(float gain)
    {
        if (!float.IsFinite(gain) || gain < 0) gain = 1f;
        Gain = GainTarget = gain;
    }

    /// <summary>Reads up to <paramref name="frames"/> stereo frames at the given speed. Never throws.</summary>
    public int Read(float[] buffer, int offsetFrames, int frames, float speed)
    {
        if (_failed || Volatile.Read(ref _disposed) != 0) return 0;
        try
        {
            _varispeed.Speed = speed;
            return _varispeed.Read(buffer, offsetFrames * 2, frames * 2) / 2;
        }
        catch (Exception ex)
        {
            _failed = true;
            _varispeed.MarkEnded(ex);
            return 0;
        }
    }

    /// <summary>Repositions the track (file time). Never throws: an unseekable file is treated as ended.</summary>
    public void Seek(TimeSpan position)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
        try
        {
            if (_ahead != null) _ahead.Seek(position); // the decoder repositions the file, not the caller
            else if (_reader != null) _reader.CurrentTime = position;
            _varispeed.Reset((long)Math.Round(position.TotalSeconds * _sampleRate));
            _failed = false;
        }
        catch (Exception ex)
        {
            _failed = true;
            _varispeed.MarkEnded(ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _ahead?.Dispose();
            _reader?.Dispose();
        }
        catch
        {
            // Releasing a broken decoder must never crash the caller.
        }
    }
}

/// <summary>Linear 0..1 ramp advanced once per frame.</summary>
internal struct Ramp
{
    public float Value;
    public float Target;
    public float Step;

    public readonly bool IsSteady => Value == Target;

    public void Set(float value)
    {
        Value = Target = value;
        Step = 0f;
    }

    /// <summary>Moves toward <paramref name="target"/> at a rate of one full 0..1 swing per <paramref name="frames"/>.</summary>
    public void To(float target, int frames)
    {
        Target = target;
        if (frames <= 0)
        {
            Value = target;
            Step = 0f;
            return;
        }
        Step = 1f / frames;
    }

    public float Next()
    {
        if (Value != Target)
        {
            if (Value < Target)
            {
                Value += Step;
                if (Value > Target) Value = Target;
            }
            else
            {
                Value -= Step;
                if (Value < Target) Value = Target;
            }
        }
        return Value;
    }
}
