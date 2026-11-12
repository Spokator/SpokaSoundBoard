using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Decodes an audio file ahead of time on the thread pool, so the audio thread only ever copies samples that are
/// already in memory: no disk access and no decoding in the audio callback. A slow disk, an antivirus scan or a
/// cloud file that has to be downloaded first can no longer stall the microphone.
/// </summary>
/// <remarks>
/// The source must be stereo float at the mixer rate (see <see cref="AudioFileLoader.Open"/>). A region
/// (start / end) and seamless looping are handled here, on the decoding side. <see cref="Read"/> runs on the audio
/// thread: short lock, no allocation; it returns 0 only at the real end of the stream. If the decoder falls behind,
/// it outputs a little silence and reports it to <see cref="RenderMonitor"/>.
/// </remarks>
public sealed class DecodeAhead : ISampleProvider, IDisposable, IThreadPoolWorkItem
{
    private const int ChunkFrames = 4096;
    private const int SilenceFrames = 256;

    private readonly WaveStream? _reader;
    private readonly ISampleProvider _source;
    private readonly int _sampleRate;
    private readonly TimeSpan _start;
    private readonly long _passBudget; // frames per pass, -1 = until the end of the file
    private readonly bool _loop;
    private readonly float[] _chunk = new float[ChunkFrames * 2];

    private readonly object _lock = new();
    private readonly float[] _ring;
    private int _read, _write, _count;
    private bool _eof, _filling, _disposed, _readerDisposed;
    private TimeSpan? _seekTarget;
    private bool _awaitingData;
    private int _generation;

    // Decoding side only (one fill at a time).
    private long _passFrames;
    private bool _passEmpty = true;

    /// <summary>
    /// Tests that render blocks much faster than real time set this so that new decoders decode on demand inside
    /// <see cref="Read"/>, deterministically. Never set in the application: it would decode on the audio thread.
    /// </summary>
    internal static bool InlineByDefault;

    private readonly bool _inline;

    /// <param name="reader">The file reader (repositioned for seeks and loops), or null for a source that cannot seek.</param>
    /// <param name="source">Stereo float samples at <paramref name="sampleRate"/>, read from <paramref name="reader"/>.</param>
    /// <param name="bufferSeconds">How much audio is kept decoded in advance.</param>
    /// <param name="start">Position of the first sample (the reader must already be there).</param>
    /// <param name="passFrames">Frames to play from <paramref name="start"/> (-1 = until the end of the file).</param>
    /// <param name="loop">Start again from <paramref name="start"/> at the end, without any gap.</param>
    public DecodeAhead(WaveStream? reader, ISampleProvider source, int sampleRate, double bufferSeconds,
        TimeSpan start = default, long passFrames = -1, bool loop = false)
        : this(reader, source, sampleRate, bufferSeconds, start, passFrames, loop, InlineByDefault)
    {
    }

    internal DecodeAhead(WaveStream? reader, ISampleProvider source, int sampleRate, double bufferSeconds,
        TimeSpan start, long passFrames, bool loop, bool inline)
    {
        _inline = inline;
        if (source.WaveFormat.Channels != 2) throw new ArgumentException("Source stéréo attendue.", nameof(source));
        _reader = reader;
        _source = source;
        _sampleRate = sampleRate;
        _start = start;
        _passBudget = passFrames;
        _loop = loop && reader != null;
        var frames = Math.Max(ChunkFrames * 2, (int)(bufferSeconds * sampleRate));
        _ring = new float[frames * 2];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        Duration = reader?.TotalTime ?? TimeSpan.Zero;
    }

    /// <summary>Length of the file (read once: the reader belongs to the decoding thread afterwards).</summary>
    public TimeSpan Duration { get; }

    /// <summary>Opens a file, positions it and decodes the first <paramref name="prefillSeconds"/> before returning.
    /// Blocking I/O: never call this on the audio thread.</summary>
    public static DecodeAhead Open(string path, int sampleRate, double bufferSeconds, double prefillSeconds,
        double startSeconds = 0, double endSeconds = 0, bool loop = false)
    {
        var (reader, provider) = AudioFileLoader.Open(path, sampleRate);
        try
        {
            var start = TimeSpan.FromSeconds(Math.Max(0, startSeconds));
            if (start > TimeSpan.Zero && start < reader.TotalTime) reader.CurrentTime = start;
            else start = TimeSpan.Zero;
            long pass = -1;
            if (endSeconds > 0 && endSeconds > start.TotalSeconds)
                pass = (long)((endSeconds - start.TotalSeconds) * sampleRate);
            var ahead = new DecodeAhead(reader, provider, sampleRate, bufferSeconds, start, pass, loop);
            ahead.Prefill(prefillSeconds);
            return ahead;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Error that ended the stream early (unreadable file), if any.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Frames decoded and waiting to be played.</summary>
    public int BufferedFrames
    {
        get { lock (_lock) return _count / 2; }
    }

    public int CapacityFrames => _ring.Length / 2;

    /// <summary>True while a seek is in progress and its first samples are not decoded yet.</summary>
    public bool IsWaiting
    {
        get
        {
            if (_inline) FillInline(0);
            lock (_lock) return _seekTarget != null || _awaitingData;
        }
    }

    /// <summary>True once everything was decoded and played.</summary>
    public bool IsEnded
    {
        get { lock (_lock) return _eof && _count == 0 && _seekTarget == null && !_awaitingData; }
    }

    /// <summary>Decodes synchronously until <paramref name="seconds"/> are buffered (or the end), then lets the
    /// thread pool fill the rest.</summary>
    public void Prefill(double seconds)
    {
        var target = Math.Min(CapacityFrames, (int)(seconds * _sampleRate)) * 2;
        while (true)
        {
            lock (_lock)
                if (_disposed || _eof || _count >= target || _ring.Length - _count < _chunk.Length) break;
            FillOnce(null, Volatile.Read(ref _generation));
        }
        Schedule();
    }

    /// <summary>Moves to <paramref name="position"/> (file time). Any thread. Until the new samples are decoded,
    /// <see cref="IsWaiting"/> is true and <see cref="Read"/> outputs silence.</summary>
    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        lock (_lock)
        {
            if (_disposed) return;
            _generation++;
            _seekTarget = position;
            _awaitingData = true;
            _read = _write = _count = 0;
            _eof = false;
        }
        Schedule();
    }

    /// <summary>Audio thread: copies decoded samples. Returns 0 only at the end of the stream.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        if (_inline) FillInline(count);
        int n;
        var schedule = false;
        lock (_lock)
        {
            if (_disposed) return 0;
            if (_seekTarget != null || _awaitingData)
            {
                n = Math.Min(count, SilenceFrames * 2);
                Array.Clear(buffer, offset, n);
                return n;
            }

            n = Math.Min(count, _count);
            var first = Math.Min(n, _ring.Length - _read);
            Array.Copy(_ring, _read, buffer, offset, first);
            Array.Copy(_ring, 0, buffer, offset + first, n - first);
            _read = (_read + n) % _ring.Length;
            _count -= n;

            if (n == 0 && !_eof)
            {
                // The decoder is late (very slow disk): a short silence rather than blocking the audio thread.
                RenderMonitor.ReportDecodeUnderrun();
                n = Math.Min(count, SilenceFrames * 2);
                Array.Clear(buffer, offset, n);
            }
            if (!_inline && !_filling && !_eof && _count < _ring.Length * 3 / 4)
            {
                _filling = true;
                schedule = true;
            }
        }
        if (schedule) ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        return n;
    }

    /// <summary>Test mode: applies a pending seek and decodes until <paramref name="samples"/> are available.</summary>
    private void FillInline(int samples)
    {
        while (true)
        {
            TimeSpan? seek;
            int generation;
            lock (_lock)
            {
                if (_disposed) return;
                seek = _seekTarget;
                _seekTarget = null;
                generation = _generation;
                if (seek == null && (_eof || _count >= samples && !_awaitingData || _ring.Length - _count < _chunk.Length)) return;
            }
            FillOnce(seek, generation);
        }
    }

    private void Schedule()
    {
        lock (_lock)
        {
            if (_inline || _filling || _disposed) return;
            _filling = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    /// <summary>Thread pool: decodes until the buffer is full, applying seeks as they come.</summary>
    void IThreadPoolWorkItem.Execute()
    {
        while (true)
        {
            TimeSpan? seek;
            int generation;
            lock (_lock)
            {
                if (_disposed)
                {
                    _filling = false;
                    break;
                }
                seek = _seekTarget;
                _seekTarget = null;
                generation = _generation;
                if (seek == null && (_eof || _ring.Length - _count < _chunk.Length))
                {
                    _filling = false;
                    return;
                }
            }
            FillOnce(seek, generation);
        }
        DisposeReader();
    }

    /// <summary>Applies a seek if any, then decodes one chunk into the ring (dropped if a newer seek arrived).</summary>
    private void FillOnce(TimeSpan? seek, int generation)
    {
        int frames;
        if (seek is { } position)
        {
            try
            {
                if (_reader != null)
                {
                    if (_reader.TotalTime > TimeSpan.Zero && position > _reader.TotalTime) position = _reader.TotalTime;
                    _reader.CurrentTime = position;
                }
                _passFrames = (long)Math.Round((position - _start).TotalSeconds * _sampleRate);
                if (_passFrames < 0) _passFrames = 0;
                _passEmpty = true;
                Error = null;
                frames = DecodeChunk();
            }
            catch (Exception ex)
            {
                Error = ex;
                frames = 0;
            }
        }
        else
        {
            frames = DecodeChunk();
        }

        lock (_lock)
        {
            if (_disposed || generation != _generation) return;
            if (frames <= 0)
            {
                _eof = true;
            }
            else
            {
                var n = frames * 2;
                var first = Math.Min(n, _ring.Length - _write);
                Array.Copy(_chunk, 0, _ring, _write, first);
                Array.Copy(_chunk, first, _ring, 0, n - first);
                _write = (_write + n) % _ring.Length;
                _count += n;
            }
            _awaitingData = false;
        }
    }

    /// <summary>Decodes up to one chunk into <see cref="_chunk"/>, honouring the region and the loop. 0 = end.</summary>
    private int DecodeChunk()
    {
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var want = ChunkFrames;
                if (_passBudget >= 0) want = (int)Math.Min(want, _passBudget - _passFrames);
                var got = want > 0 ? _source.Read(_chunk, 0, want * 2) / 2 : 0;
                if (got > 0)
                {
                    _passFrames += got;
                    _passEmpty = false;
                    return got;
                }
                // End of the pass: loop back to the start unless the pass produced nothing at all.
                if (!_loop || _passEmpty) return 0;
                _reader!.CurrentTime = _start;
                _passFrames = 0;
                _passEmpty = true;
            }
            return 0;
        }
        catch (Exception ex)
        {
            // Corrupted / unreadable file: the stream ends here.
            Error = ex;
            return 0;
        }
    }

    public void Dispose()
    {
        bool disposeNow;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            disposeNow = !_filling;
        }
        // While a fill is running it owns the reader: it closes it when it sees the flag.
        if (disposeNow) DisposeReader();
    }

    private void DisposeReader()
    {
        lock (_lock)
        {
            if (_readerDisposed) return;
            _readerDisposed = true;
        }
        try
        {
            _reader?.Dispose();
        }
        catch
        {
            // Releasing a broken decoder must never crash the caller.
        }
    }
}
