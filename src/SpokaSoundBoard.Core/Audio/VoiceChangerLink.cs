namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Between the mixer and the voice changer: the audio thread hands the microphone over (<see cref="Input"/>) and gets
/// the converted voice back (<see cref="Output"/>), which then goes through the usual voice chain (effects, radio,
/// gate). The converted voice comes in bursts (one block every few hundred milliseconds): playback only starts once
/// <see cref="PrimeSamples"/> are buffered, and starts over after a gap. Too much buffered (after a hiccup) is
/// dropped so the delay never creeps up.
/// </summary>
public sealed class VoiceChangerLink
{
    private float[] _silence = new float[2048];
    private bool _primed;
    private volatile int _primeSamples = 48000 / 2;

    /// <summary>Microphone at 48 kHz, read by the voice changer session.</summary>
    public FloatRingBuffer Input { get; } = new(48000 * 4);

    /// <summary>Converted voice at 48 kHz, written by the voice changer session.</summary>
    public FloatRingBuffer Output { get; } = new(48000 * 6);

    /// <summary>Buffered audio needed before playing (one block plus a safety margin).</summary>
    public int PrimeSamples
    {
        get => _primeSamples;
        set => _primeSamples = Math.Clamp(value, 480, 48000 * 3);
    }

    /// <summary>Extra audio tolerated above <see cref="PrimeSamples"/> before catching up.</summary>
    public int MaxExtraSamples { get; set; } = 48000 * 2 / 5;

    /// <summary>Times the converted voice ran out (the GPU did not keep up).</summary>
    public int Underruns => _underruns;

    private int _underruns;

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

        var available = Output.Count;
        if (!_primed)
        {
            if (available < _primeSamples)
            {
                mic.Clear();
                return;
            }
            _primed = true;
        }
        if (available > _primeSamples + MaxExtraSamples) Output.Skip(available - _primeSamples);

        var read = Output.Read(mic);
        if (read < mic.Length)
        {
            mic[read..].Clear();
            _primed = false;
            Interlocked.Increment(ref _underruns);
        }
    }

    /// <summary>New session or new voice: forget everything buffered.</summary>
    public void Reset()
    {
        Input.Clear();
        Output.Clear();
        _primed = false;
    }
}
