using NAudio.Wave;
using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Music engine: pulled by <see cref="MixerCore"/> on the audio thread, always returns a full buffer (silence when
/// idle). Several decks can sound at once: automatic equal-power crossfade (or sample-exact gapless chaining)
/// toward a preloaded next track, short crossfade on manual track changes, micro-fades on pause/play/seek/stop,
/// varispeed per deck and an optional real-time effects chain plus a spectrum tap on the output.
/// </summary>
/// <remarks>
/// Threading: UI calls only take <c>_lock</c> for short state swaps (files are opened outside of it, the next track
/// on a worker task). Files are decoded ahead on the thread pool (<see cref="DecodeAhead"/>): the audio thread holds
/// the lock while it copies and mixes one block, without disk access or decoding. Retired decks are disposed on the thread
/// pool, never on the audio thread. Events are raised outside the lock: <see cref="TrackEnded"/> and
/// <see cref="TrackAdvanced"/> on the audio thread, <see cref="NextTrackFailed"/> on a worker thread.
/// </remarks>
public sealed class MusicPlayer : ISampleProvider, IDisposable
{
    /// <summary>Automatic crossfades are skipped when a track is shorter than this (or than 3× the fade).</summary>
    public const double MinCrossfadeTrackSeconds = 20;

    public static readonly TimeSpan MaxCrossfade = TimeSpan.FromSeconds(12);

    private const int MaxOutgoing = 6;
    private const int GraveyardSize = 16;

    private readonly object _lock = new();
    private readonly object _disposeLock = new();
    private readonly int _sampleRate;
    private readonly int _loadFadeFrames, _pauseFadeFrames, _seekFadeFrames, _stopFadeFrames;
    private readonly float _gainSmoothing;

    // Deck state (under _lock).
    private MusicDeck? _current;
    private readonly MusicDeck?[] _outgoing = new MusicDeck?[MaxOutgoing];
    private int _outgoingCount;
    private MusicDeck? _next;
    private volatile string? _nextPath;
    private float _nextGain = 1f;
    private int _nextGeneration;
    private bool _nextOpening;
    private bool _awaitingNext;
    private volatile bool _playing;
    private Ramp _master;
    private float _gain = 1f;
    private bool _disposed;

    private long _crossfadeTicks;
    private volatile MusicEffectSettings? _effects;
    private readonly MusicEffectsChain _chain;
    private float[] _scratch = new float[4800 * 2];

    // Retired decks waiting to be disposed off the audio thread (under _lock).
    private readonly MusicDeck?[] _graveyard = new MusicDeck?[GraveyardSize];
    private int _graveyardCount;
    private int _drainQueued;
    private readonly Drainer _drainer;

    public MusicPlayer(int sampleRate)
    {
        _sampleRate = sampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _loadFadeFrames = sampleRate * 300 / 1000;
        _pauseFadeFrames = sampleRate * 20 / 1000;
        _seekFadeFrames = sampleRate * 15 / 1000;
        _stopFadeFrames = sampleRate * 30 / 1000;
        _gainSmoothing = 1f - MathF.Exp(-1f / (0.01f * sampleRate));
        _chain = new MusicEffectsChain(sampleRate);
        Spectrum = new SpectrumAnalyzer(sampleRate);
        _drainer = new Drainer(this);
    }

    public WaveFormat WaveFormat { get; }

    // ---- state ------------------------------------------------------------------------------------------------

    /// <summary>File of the current track (the incoming one as soon as a crossfade starts), null when stopped.</summary>
    public string? CurrentFile { get; private set; }

    /// <summary>Track queued with <see cref="SetNext"/> (opening or ready), null when none.</summary>
    public string? NextFile => _nextPath;

    /// <summary>True once the queued next track is opened and ready to start.</summary>
    public bool IsNextReady
    {
        get { lock (_lock) return _next != null; }
    }

    /// <summary>Transport state requested by the user (false as soon as Pause/Stop is called or a track ends).</summary>
    public bool IsPlaying => _playing;

    /// <summary>True while something is audible: playing, or still fading out after Pause/Stop.</summary>
    public bool IsActive
    {
        get { lock (_lock) return _playing || _master.Value > 0f; }
    }

    public bool HasTrack
    {
        get { lock (_lock) return _current != null; }
    }

    /// <summary>True while an older deck is fading out under the current one (automatic or manual crossfade).</summary>
    public bool IsCrossfading
    {
        get { lock (_lock) return _outgoingCount > 0 && _current != null; }
    }

    /// <summary>Linear gain of the current track (loudness normalization). Changes are smoothed (~10 ms).</summary>
    public float Gain
    {
        get { lock (_lock) return _gain; }
        set
        {
            if (!float.IsFinite(value) || value < 0) value = 1f;
            lock (_lock)
            {
                _gain = value;
                if (_current != null) _current.GainTarget = value;
            }
        }
    }

    /// <summary>
    /// Automatic crossfade between consecutive tracks (equal power). Zero = gapless: the next track starts on the
    /// exact sample after the last one of the previous track. Clamped to 0..12 s.
    /// </summary>
    public TimeSpan CrossfadeDuration
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _crossfadeTicks));
        set
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            if (value > MaxCrossfade) value = MaxCrossfade;
            Volatile.Write(ref _crossfadeTicks, value.Ticks);
        }
    }

    /// <summary>Music effects read live on every block (null = none). Leave null on the preview player.</summary>
    public MusicEffectSettings? Effects
    {
        get => _effects;
        set => _effects = value;
    }

    /// <summary>Spectrum of the player output (after effects), for a visualizer.</summary>
    public SpectrumAnalyzer Spectrum { get; }

    /// <summary>Position in track (file) time, whatever the speed. Reports the target of a pending seek.</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                var deck = _current;
                if (deck == null) return TimeSpan.Zero;
                if (deck.PendingSeek is { } pending) return pending;
                return ClampToDuration(deck, TimeSpan.FromSeconds(deck.PositionSeconds));
            }
        }
    }

    public TimeSpan Duration
    {
        get { lock (_lock) return _current?.Duration ?? TimeSpan.Zero; }
    }

    /// <summary>Duration − Position, in track time.</summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
            {
                var deck = _current;
                if (deck == null) return TimeSpan.Zero;
                var position = deck.PendingSeek?.TotalSeconds ?? deck.PositionSeconds;
                return TimeSpan.FromSeconds(Math.Max(0, deck.DurationSeconds - position));
            }
        }
    }

    /// <summary>Real (wall-clock) time left before the end of the current track, accounting for the speed.</summary>
    public TimeSpan RemainingRealTime
    {
        get
        {
            var speed = EffectiveSpeed(_effects);
            return TimeSpan.FromSeconds(Remaining.TotalSeconds / speed);
        }
    }

    /// <summary>Raised on the audio thread when the current track reaches its end and nothing is queued.</summary>
    public event Action? TrackEnded;

    /// <summary>
    /// Raised on the audio thread when the player moved by itself to the queued track (start of the crossfade, or
    /// first sample of a gapless transition). The argument is the path of the track that is now current.
    /// </summary>
    public event Action<string>? TrackAdvanced;

    /// <summary>Raised on a worker thread when the track given to <see cref="SetNext"/> cannot be opened.</summary>
    public event Action<string, Exception>? NextTrackFailed;

    // ---- commands ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a track (synchronously, throws if the file cannot be opened) and makes it current, keeping the current
    /// <see cref="Gain"/>. While music is playing, the old track fades out over ~300 ms under the new one. Cancels
    /// the queued next track (reused directly when it is the same file).
    /// </summary>
    public void Load(string path, bool autoPlay = true) => LoadCore(path, null, autoPlay);

    /// <summary>Same as <see cref="Load(string, bool)"/> with the loudness-normalization gain of the new track.</summary>
    public void Load(string path, float gain, bool autoPlay = true) => LoadCore(path, gain, autoPlay);

    private void LoadCore(string path, float? gain, bool autoPlay)
    {
        ArgumentNullException.ThrowIfNull(path);
        MusicDeck? deck = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_next != null && PathEquals(_nextPath, path))
            {
                deck = _next;
                _next = null;
            }
        }

        deck ??= MusicDeck.Open(path, _sampleRate); // may throw: nothing changed yet
        Install(deck, gain, autoPlay);
    }

    /// <summary>Makes an opened deck current (internal: lets the tests inject decks with custom sources).</summary>
    internal void Install(MusicDeck deck, float? gain, bool autoPlay)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                deck.Dispose();
                throw new ObjectDisposedException(nameof(MusicPlayer));
            }

            CancelNextLocked();
            var g = gain ?? _gain;
            deck.SetGain(g);
            _gain = deck.GainTarget;
            deck.PendingSeek = null;
            deck.RampWhenReady = false;
            deck.SeekRamp.Set(1f);

            var old = _current;
            var audible = old != null && _playing && _master.Value > 0f && !_awaitingNext && !old.Ended
                          && old.Fade.Value > 0f;
            if (old != null)
            {
                if (audible)
                {
                    AddOutgoing(old);
                    old.Fade.To(0f, _loadFadeFrames);
                }
                else if (_master.Value > 0f && !old.Ended)
                {
                    AddOutgoing(old);
                    old.Fade.To(0f, _seekFadeFrames);
                }
                else
                {
                    Retire(old);
                }
            }

            _current = deck;
            CurrentFile = deck.Path;
            _awaitingNext = false;
            if (audible)
            {
                deck.Fade.Set(0f);
                deck.Fade.To(1f, _loadFadeFrames);
            }
            else
            {
                deck.Fade.Set(1f);
            }

            _playing = autoPlay;
            if (autoPlay)
            {
                if (_master.Value == 0f)
                {
                    // Nothing audible: start on the very first sample.
                    RetireAllOutgoing();
                    _master.Set(1f);
                }
                else
                {
                    _master.To(1f, _pauseFadeFrames);
                }
            }
            else
            {
                _master.To(0f, _pauseFadeFrames);
            }
        }
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_current == null || _disposed) return;
            _playing = true;
            _master.To(1f, _pauseFadeFrames);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _playing = false;
            _master.To(0f, _pauseFadeFrames);
        }
    }

    /// <summary>Fades out quickly (~30 ms), then releases every file. Also cancels the queued next track.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _playing = false;
            _awaitingNext = false;
            CancelNextLocked();
            var old = _current;
            _current = null;
            CurrentFile = null;
            if (_master.Value > 0f)
            {
                for (var i = 0; i < _outgoingCount; i++) _outgoing[i]!.Fade.To(0f, _stopFadeFrames);
                if (old != null)
                {
                    AddOutgoing(old);
                    old.Fade.To(0f, _stopFadeFrames);
                }
                _master.To(0f, _stopFadeFrames);
            }
            else
            {
                if (old != null) Retire(old);
                RetireAllOutgoing();
            }
        }
    }

    /// <summary>Moves the current track to <paramref name="position"/> (file time) with a ~15 ms micro-fade.</summary>
    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            var deck = _current;
            if (deck == null) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > deck.Duration) position = deck.Duration;
            _awaitingNext = false;

            var audible = _master.Value > 0f && deck.Fade.Value > 0f && !deck.Ended;
            if (audible)
            {
                deck.PendingSeek = position;
                deck.SeekRamp.To(0f, _seekFadeFrames);
            }
            else
            {
                deck.PendingSeek = null;
                deck.Seek(position);
                if (deck.IsAsync)
                {
                    // The decoder is still positioning the file: rise from silence once its samples are ready.
                    deck.SeekRamp.Set(0f);
                    deck.RampWhenReady = true;
                }
                else
                {
                    deck.SeekRamp.Set(1f);
                }
            }
        }
    }

    /// <summary>
    /// Queues the track that follows the current one (null cancels). The file is opened on a worker task; once it is
    /// ready the player starts it by itself at (end − crossfade) and raises <see cref="TrackAdvanced"/>. If it cannot
    /// be opened, <see cref="NextTrackFailed"/> is raised and the player behaves as if nothing was queued.
    /// </summary>
    public void SetNext(string? path, float gain = 1f)
    {
        if (!float.IsFinite(gain) || gain < 0) gain = 1f;
        MusicDeck? stale = null;
        int generation;
        lock (_lock)
        {
            if (_disposed) return;
            if (path != null && PathEquals(_nextPath, path) && (_next != null || _nextOpening))
            {
                // Same track already queued: only the gain changes.
                _nextGain = gain;
                _next?.SetGain(gain);
                return;
            }

            generation = ++_nextGeneration;
            stale = _next;
            _next = null;
            _nextPath = path;
            _nextGain = gain;
            _nextOpening = path != null;
        }
        stale?.Dispose();
        if (path == null) return;

        _ = Task.Run(() =>
        {
            MusicDeck? deck = null;
            Exception? error = null;
            try
            {
                deck = MusicDeck.Open(path, _sampleRate);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            var report = false;
            lock (_lock)
            {
                if (generation == _nextGeneration && !_disposed)
                {
                    _nextOpening = false;
                    if (deck != null)
                    {
                        deck.SetGain(_nextGain);
                        _next = deck;
                        deck = null;
                    }
                    else
                    {
                        _nextPath = null;
                        report = true;
                    }
                }
            }
            deck?.Dispose(); // cancelled meanwhile
            if (report && error != null) Raise(NextTrackFailed, path, error);
        });
    }

    // ---- audio thread -----------------------------------------------------------------------------------------

    public int Read(float[] buffer, int offset, int count)
    {
        string? advanced = null;
        var ended = false;
        var output = buffer.AsSpan(offset, count);
        try
        {
            lock (_lock)
            {
                if (_disposed) output.Clear();
                else Render(buffer, offset, count, ref advanced, ref ended);
            }
        }
        catch
        {
            // Never let an exception escape the audio thread.
            output.Clear();
        }

        try
        {
            _chain.Process(output, _effects);
        }
        catch
        {
            output.Clear();
            _chain.Reset();
        }
        Spectrum.Write(output);

        if (advanced != null) Raise(TrackAdvanced, advanced);
        if (ended) Raise(TrackEnded);
        return count;
    }

    private void Render(float[] buffer, int offset, int count, ref string? advanced, ref bool ended)
    {
        var frames = count / 2;
        var output = buffer.AsSpan(offset, count);
        output.Clear();
        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];

        if (!_playing && _master.Value == 0f)
        {
            // Paused or stopped and silent: decks are frozen, nothing left to fade.
            RetireAllOutgoing();
            return;
        }

        var speed = EffectiveSpeed(_effects);

        if (_awaitingNext)
        {
            if (_next != null)
            {
                AdvanceGapless();
                advanced = _current!.Path;
            }
            else if (!_nextOpening)
            {
                // The queued track failed or was cancelled: behave as if nothing was queued.
                _awaitingNext = false;
                _playing = false;
                _master.To(0f, _stopFadeFrames);
                ended = true;
            }
        }

        var deck = _current;
        if (deck is { PendingSeek: { } target } && deck.SeekRamp.Value == 0f)
        {
            // Faded out: reposition. A background decoder does the file work; the deck stays silent until it is done.
            deck.Seek(target);
            deck.PendingSeek = null;
            if (deck.IsAsync) deck.RampWhenReady = true;
            else deck.SeekRamp.To(1f, _seekFadeFrames);
        }
        if (deck is { RampWhenReady: true } && !deck.Waiting)
        {
            deck.RampWhenReady = false;
            deck.SeekRamp.To(1f, _seekFadeFrames);
        }

        // Automatic crossfade toward the queued track.
        if (deck != null && _next != null && _playing && !_awaitingNext && deck.PendingSeek == null
            && deck.Fade.Target > 0f && !deck.Ended)
        {
            var fade = EffectiveCrossfadeSeconds(deck, _next);
            if (fade > 0)
            {
                var remaining = (deck.DurationSeconds - deck.PositionSeconds) / speed;
                if (remaining <= fade)
                {
                    var fadeFrames = (int)(remaining * _sampleRate);
                    if (fadeFrames > frames)
                    {
                        StartCrossfade(fadeFrames);
                        advanced = _current!.Path;
                        deck = _current;
                    }
                }
            }
        }

        for (var i = 0; i < _outgoingCount;)
        {
            var old = _outgoing[i]!;
            RenderDeck(old, output, 0, frames, speed);
            if (old.Ended || old.Fade is { Value: 0f, Target: 0f }) RemoveOutgoingAt(i);
            else i++;
        }

        var position = 0;
        while (deck != null && !_awaitingNext && position < frames && (_playing || _master.Value > 0f))
        {
            position += RenderDeck(deck, output, position, frames - position, speed);
            if (position >= frames || !deck.Ended) break;

            if (_next != null)
            {
                // Gapless: the next track continues on the very next sample.
                AdvanceGapless();
                advanced = _current!.Path;
                deck = _current;
                continue;
            }
            if (_nextOpening)
            {
                _awaitingNext = true; // finish in silence until the queued file is open
                break;
            }
            if (_playing)
            {
                _playing = false;
                ended = true;
            }
            break;
        }

        if (!_master.IsSteady || _master.Value != 1f)
        {
            for (var i = 0; i < output.Length; i += 2)
            {
                var g = _master.Next();
                output[i] *= g;
                output[i + 1] *= g;
            }
        }

        // After the end of the track (the rest of the block is already silent): let fading decks die out quickly.
        if (ended) _master.To(0f, _stopFadeFrames);
    }

    /// <summary>Reads one deck and adds it to <paramref name="output"/>. Returns the frames produced.</summary>
    private int RenderDeck(MusicDeck deck, Span<float> output, int offsetFrames, int frames, float speed)
    {
        // A seek is being decoded: this block is silent for the deck, and its position does not move.
        if (deck.Waiting) return frames;
        var n = deck.Read(_scratch, 0, frames, speed);
        if (n <= 0) return 0;
        var o = offsetFrames * 2;
        var src = _scratch;

        if (deck.Fade is { Value: 1f, Target: 1f } && deck.SeekRamp is { Value: 1f, Target: 1f } && deck.Gain == deck.GainTarget)
        {
            var g = deck.Gain;
            if (g == 1f)
                for (var i = 0; i < n * 2; i++) output[o + i] += src[i];
            else
                for (var i = 0; i < n * 2; i++) output[o + i] += src[i] * g;
            return n;
        }

        for (var f = 0; f < n; f++)
        {
            if (deck.Gain != deck.GainTarget)
            {
                deck.Gain += (deck.GainTarget - deck.Gain) * _gainSmoothing;
                if (MathF.Abs(deck.GainTarget - deck.Gain) < 1e-5f) deck.Gain = deck.GainTarget;
            }
            var x = deck.Fade.Next();
            var curve = x >= 1f ? 1f : x <= 0f ? 0f : MathF.Sin(x * (MathF.PI / 2f)); // equal power
            var g = deck.Gain * curve * deck.SeekRamp.Next();
            output[o + f * 2] += src[f * 2] * g;
            output[o + f * 2 + 1] += src[f * 2 + 1] * g;
        }
        return n;
    }

    private void StartCrossfade(int fadeFrames)
    {
        var old = _current!;
        var next = _next!;
        _next = null;
        _nextPath = null;
        AddOutgoing(old);
        old.Fade.To(0f, fadeFrames);
        next.Fade.Set(0f);
        next.Fade.To(1f, fadeFrames);
        next.SeekRamp.Set(1f);
        MakeCurrent(next);
    }

    private void AdvanceGapless()
    {
        var old = _current;
        var next = _next!;
        _next = null;
        _nextPath = null;
        _awaitingNext = false;
        if (old != null)
        {
            next.Fade = old.Fade; // keep the envelope continuous
            Retire(old);
        }
        next.SeekRamp.Set(1f);
        MakeCurrent(next);
    }

    private void MakeCurrent(MusicDeck deck)
    {
        _current = deck;
        CurrentFile = deck.Path;
        _gain = deck.GainTarget;
    }

    private double EffectiveCrossfadeSeconds(MusicDeck current, MusicDeck next)
    {
        var fade = CrossfadeDuration.TotalSeconds;
        if (fade <= 0) return 0;
        var min = Math.Max(3 * fade, MinCrossfadeTrackSeconds);
        return current.DurationSeconds < min || next.DurationSeconds < min ? 0 : fade;
    }

    private static float EffectiveSpeed(MusicEffectSettings? effects)
    {
        if (effects is not { Enabled: true }) return 1f;
        var speed = effects.Speed;
        return float.IsFinite(speed) ? Math.Clamp(speed, VarispeedProvider.MinSpeed, VarispeedProvider.MaxSpeed) : 1f;
    }

    // ---- deck bookkeeping (under _lock) -----------------------------------------------------------------------

    private void CancelNextLocked()
    {
        _nextGeneration++;
        _nextOpening = false;
        _nextPath = null;
        if (_next != null)
        {
            Retire(_next);
            _next = null;
        }
    }

    private void AddOutgoing(MusicDeck deck)
    {
        if (_outgoingCount == MaxOutgoing)
        {
            // Too many overlapping fades (frantic skipping): drop the quietest one.
            var quietest = 0;
            for (var i = 1; i < _outgoingCount; i++)
                if (_outgoing[i]!.Fade.Value < _outgoing[quietest]!.Fade.Value) quietest = i;
            RemoveOutgoingAt(quietest);
        }
        deck.PendingSeek = null;
        _outgoing[_outgoingCount++] = deck;
    }

    private void RemoveOutgoingAt(int index)
    {
        var deck = _outgoing[index]!;
        for (var i = index; i < _outgoingCount - 1; i++) _outgoing[i] = _outgoing[i + 1];
        _outgoing[--_outgoingCount] = null;
        Retire(deck);
    }

    private void RetireAllOutgoing()
    {
        while (_outgoingCount > 0) RemoveOutgoingAt(_outgoingCount - 1);
    }

    /// <summary>Hands a deck that is no longer referenced to the thread pool for disposal.</summary>
    private void Retire(MusicDeck deck)
    {
        if (_graveyardCount < GraveyardSize)
        {
            _graveyard[_graveyardCount++] = deck;
            if (Interlocked.Exchange(ref _drainQueued, 1) == 0)
                ThreadPool.UnsafeQueueUserWorkItem(_drainer, preferLocal: false);
        }
        else
        {
            deck.Dispose(); // should never happen: the pool is far behind
        }
    }

    private void DrainGraveyard()
    {
        lock (_disposeLock)
        {
            Volatile.Write(ref _drainQueued, 0);
            while (true)
            {
                MusicDeck? deck;
                lock (_lock)
                {
                    if (_graveyardCount == 0) return;
                    deck = _graveyard[--_graveyardCount];
                    _graveyard[_graveyardCount] = null;
                }
                deck?.Dispose();
            }
        }
    }

    private sealed class Drainer(MusicPlayer owner) : IThreadPoolWorkItem
    {
        public void Execute() => owner.DrainGraveyard();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static TimeSpan ClampToDuration(MusicDeck deck, TimeSpan position) =>
        position < TimeSpan.Zero ? TimeSpan.Zero : deck.Duration > TimeSpan.Zero && position > deck.Duration ? deck.Duration : position;

    private static bool PathEquals(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch
        {
            // A faulty subscriber must not kill the audio thread.
        }
    }

    private static void Raise<T>(Action<T>? handler, T arg)
    {
        try
        {
            handler?.Invoke(arg);
        }
        catch
        {
            // See above.
        }
    }

    private static void Raise<T1, T2>(Action<T1, T2>? handler, T1 a, T2 b)
    {
        try
        {
            handler?.Invoke(a, b);
        }
        catch
        {
            // See above.
        }
    }

    /// <summary>Releases every deck (current, fading, queued) synchronously.</summary>
    public void Dispose()
    {
        var decks = new List<MusicDeck>();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _playing = false;
            _nextGeneration++;
            _nextOpening = false;
            _nextPath = null;
            if (_current != null) decks.Add(_current);
            if (_next != null) decks.Add(_next);
            for (var i = 0; i < _outgoingCount; i++) decks.Add(_outgoing[i]!);
            for (var i = 0; i < _graveyardCount; i++) decks.Add(_graveyard[i]!);
            Array.Clear(_outgoing);
            Array.Clear(_graveyard);
            _outgoingCount = _graveyardCount = 0;
            _current = _next = null;
            CurrentFile = null;
            _master.Set(0f);
        }

        // Taking the dispose lock also waits for a drain that is still closing a file.
        lock (_disposeLock)
        {
            foreach (var deck in decks) deck.Dispose();
        }
    }
}
