namespace SpokaSoundBoard.Core.Dsp;

/// <summary>Feedback delay with a low-pass filter in the loop for a natural decay.</summary>
public sealed class EchoEffect
{
    private readonly float[] _buffer;
    private readonly int _sampleRate;
    private readonly Biquad _damping = new();
    private int _write;
    private float _delaySamples;
    private float _targetDelay;

    public EchoEffect(int sampleRate, float maxDelayMs = 1500f)
    {
        _sampleRate = sampleRate;
        _buffer = new float[(int)(sampleRate * maxDelayMs / 1000f) + 2];
        _damping.SetLowPass(sampleRate, 4500f);
        DelayMs = 250f;
        _delaySamples = _targetDelay;
    }

    public float DelayMs
    {
        get => _targetDelay * 1000f / _sampleRate;
        set => _targetDelay = Math.Clamp(value * _sampleRate / 1000f, 1f, _buffer.Length - 2);
    }

    public float Feedback { get; set; } = 0.35f;
    public float Mix { get; set; } = 0.3f;

    public void Reset() => Array.Clear(_buffer);

    public float Process(float input)
    {
        // Glide towards the target delay to avoid zipper noise when the user moves the slider.
        _delaySamples += (_targetDelay - _delaySamples) * 0.0005f;

        var pos = _write - _delaySamples;
        while (pos < 0) pos += _buffer.Length;
        var i0 = (int)pos;
        var frac = pos - i0;
        var i1 = i0 + 1 >= _buffer.Length ? 0 : i0 + 1;
        var delayed = _buffer[i0] + (_buffer[i1] - _buffer[i0]) * frac;

        _buffer[_write] = input + _damping.Process(delayed) * Math.Clamp(Feedback, 0f, 0.95f);
        _write++;
        if (_write >= _buffer.Length) _write = 0;

        return input + delayed * Mix;
    }
}
