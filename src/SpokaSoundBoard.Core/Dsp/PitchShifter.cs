namespace SpokaSoundBoard.Core.Dsp;

/// <summary>
/// Low-latency pitch shifter based on two cross-faded modulated delay taps.
/// </summary>
public sealed class PitchShifter
{
    private readonly float[] _buffer;
    private readonly int _window;
    private int _write;
    private float _phase;

    public PitchShifter(int sampleRate, float windowMs = 40f)
    {
        _window = Math.Max(64, (int)(sampleRate * windowMs / 1000f));
        _buffer = new float[_window + 8];
    }

    /// <summary>Playback ratio: 2 = one octave up, 0.5 = one octave down.</summary>
    public float Ratio { get; set; } = 1f;

    public void SetSemitones(float semitones) => Ratio = MathF.Pow(2f, semitones / 12f);

    public void Reset()
    {
        Array.Clear(_buffer);
        _phase = 0;
    }

    public float Process(float input)
    {
        _buffer[_write] = input;

        _phase += (1f - Ratio) / _window;
        if (_phase >= 1f) _phase -= 1f;
        else if (_phase < 0f) _phase += 1f;

        var phase2 = _phase + 0.5f;
        if (phase2 >= 1f) phase2 -= 1f;

        var s1 = ReadDelayed(_phase * _window + 1f);
        var s2 = ReadDelayed(phase2 * _window + 1f);

        // sin² windows: the two gains always sum to 1.
        var g1 = MathF.Sin(MathF.PI * _phase);
        g1 *= g1;
        var output = s1 * g1 + s2 * (1f - g1);

        _write++;
        if (_write >= _buffer.Length) _write = 0;
        return output;
    }

    private float ReadDelayed(float delay)
    {
        var pos = _write - delay;
        while (pos < 0) pos += _buffer.Length;
        var i0 = (int)pos;
        var frac = pos - i0;
        var i1 = i0 + 1;
        if (i0 >= _buffer.Length) i0 -= _buffer.Length;
        if (i1 >= _buffer.Length) i1 -= _buffer.Length;
        return _buffer[i0] + (_buffer[i1] - _buffer[i0]) * frac;
    }
}
