namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Between the mixer and the voice changer: the audio thread hands the microphone over (<see cref="Input"/>) and gets
/// the converted voice back (<see cref="Output"/>), which then goes through the usual voice chain (effects, radio,
/// gate). The converted voice comes in bursts (one block every couple of hundred milliseconds), so it works like the
/// jitter buffer of a voice call:
/// <list type="bullet">
/// <item>playback starts once <see cref="PrimeSamples"/> (one block plus a margin) are buffered;</item>
/// <item>when the converted voice runs out (the GPU was late), the margin grows by itself so it does not happen
/// again, and shrinks back slowly once things are steady;</item>
/// <item>delay that piled up is caught up only through silences, never by dropping words, except past a hard limit.</item>
/// </list>
/// Audio thread only: no allocation in steady state, no lock held for long.
/// </summary>
public sealed class VoiceChangerLink
{
    private const int Rate = 48000;

    /// <summary>Below this the converted voice is silence (the engine outputs exact zeros between phrases).</summary>
    private const float SilencePeak = 0.004f;

    /// <summary>Margin added after each gap, up to <see cref="MaxBoostSamples"/>.</summary>
    private const int BoostStepSamples = Rate * 40 / 1000;
    private const int MaxBoostSamples = Rate * 300 / 1000;

    /// <summary>Steady playback needed before the margin shrinks by one step.</summary>
    private const int SteadySamplesPerRelease = Rate * 20;

    private float[] _silence = new float[2048];
    private bool _primed;
    private bool _started; // played something since the last reset: waiting now is a gap
    private volatile int _primeSamples = Rate / 2;
    private int _boostSamples;
    private long _steadySamples;

    /// <summary>Microphone at 48 kHz, read by the voice changer session.</summary>
    public FloatRingBuffer Input { get; } = new(Rate * 4);

    /// <summary>Converted voice at 48 kHz, written by the voice changer session.</summary>
    public FloatRingBuffer Output { get; } = new(Rate * 6);

    /// <summary>Buffered audio needed before playing: one block plus the safety margin (without the automatic boost).</summary>
    public int PrimeSamples
    {
        get => _primeSamples;
        set => _primeSamples = Math.Clamp(value, 480, Rate * 3);
    }

    /// <summary>Extra audio tolerated above the target before catching up (through silences).</summary>
    public int MaxExtraSamples { get; set; } = Rate * 3 / 20;

    /// <summary>Beyond this much above the target, audio is dropped even while speaking (the delay must stay bounded).</summary>
    public int HardMaxExtraSamples { get; set; } = Rate * 3 / 2;

    /// <summary>Margin currently added after gaps, in samples.</summary>
    public int BoostSamples => Volatile.Read(ref _boostSamples);

    /// <summary>Times the converted voice ran out (the GPU did not keep up).</summary>
    public int Underruns => _underruns;

    private int _underruns;
    private long _skippedSamples, _skippedVoiceSamples, _waitingSamples;

    /// <summary>Converted audio dropped to catch up, in samples (silence, normally).</summary>
    public long SkippedSamples => Interlocked.Read(ref _skippedSamples);

    /// <summary>Part of <see cref="SkippedSamples"/> that was speech (only past the hard limit): lost words.</summary>
    public long SkippedVoiceSamples => Interlocked.Read(ref _skippedVoiceSamples);

    /// <summary>Silence played while waiting for converted voice (gaps), in samples.</summary>
    public long WaitingSamples => Interlocked.Read(ref _waitingSamples);

    /// <summary>Audio thread: sends the microphone (silence when it is off) and replaces it with the converted voice.</summary>
    public void Exchange(Span<float> mic, bool micEnabled)
    {
        if (micEnabled)
        {
            Input.Write(mic);
        }
        else
        {
            if (_silence.Length < mic.Length) _silence = new float[mic.Length]; // only when the block size grows
            Input.Write(_silence.AsSpan(0, mic.Length));
        }

        var target = _primeSamples + _boostSamples;
        var available = Output.Count;
        if (!_primed)
        {
            if (available < target)
            {
                if (_started) Interlocked.Add(ref _waitingSamples, mic.Length);
                mic.Clear();
                return;
            }
            _primed = true;
            _started = true;
        }

        // Catch up on piled-up delay, one block at a time, through silence only (pauses between phrases come often).
        var excess = available - target - MaxExtraSamples;
        if (excess > 0)
        {
            var chunk = Math.Min(excess, mic.Length);
            var voiced = Output.Peak(chunk) >= SilencePeak;
            if (!voiced || available - target > HardMaxExtraSamples)
            {
                Output.Skip(chunk);
                Interlocked.Add(ref _skippedSamples, chunk);
                if (voiced) Interlocked.Add(ref _skippedVoiceSamples, chunk);
            }
        }

        var read = Output.Read(mic);
        if (read < mic.Length)
        {
            Interlocked.Add(ref _waitingSamples, mic.Length - read);
            mic[read..].Clear();
            _primed = false;
            Interlocked.Increment(ref _underruns);
            // Late once: keep a little more ahead from now on.
            Volatile.Write(ref _boostSamples, Math.Min(MaxBoostSamples, _boostSamples + BoostStepSamples));
            _steadySamples = 0;
            return;
        }

        // Steady for a while: give the extra margin back, step by step.
        _steadySamples += read;
        if (_steadySamples >= SteadySamplesPerRelease && _boostSamples > 0)
        {
            Volatile.Write(ref _boostSamples, Math.Max(0, _boostSamples - BoostStepSamples / 2));
            _steadySamples = 0;
        }
    }

    /// <summary>New session or new voice: forget everything buffered.</summary>
    public void Reset()
    {
        Input.Clear();
        Output.Clear();
        _primed = false;
        _started = false;
        _steadySamples = 0;
        Volatile.Write(ref _boostSamples, 0);
    }
}
