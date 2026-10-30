namespace SpokaSoundBoard.Core.Dsp;

/// <summary>Two-voice chorus (modulated short delays): doubling, ghost, underwater and alien voices.</summary>
public sealed class ChorusEffect
{
    private readonly float[] _buffer;
    private readonly int _sampleRate;
    private int _write;
    private double _phase;

    public ChorusEffect(int sampleRate)
    {
        _sampleRate = sampleRate;
        _buffer = new float[(int)(sampleRate * 0.06) + 4];
    }

    /// <summary>Modulation speed in Hz.</summary>
    public float Rate { get; set; } = 0.8f;

    /// <summary>0..1, modulation depth.</summary>
    public float Depth { get; set; } = 0.5f;

    public float Mix { get; set; } = 0.5f;

    public void Reset() => Array.Clear(_buffer);

    public float Process(float input)
    {
        _buffer[_write] = input;
        _phase += 2 * Math.PI * Math.Clamp(Rate, 0.05f, 10f) / _sampleRate;
        if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;

        var baseDelay = 0.012f * _sampleRate;
        var swing = 0.008f * _sampleRate * Math.Clamp(Depth, 0f, 1f);
        var d1 = baseDelay + swing * (float)Math.Sin(_phase);
        var d2 = baseDelay + swing * (float)Math.Sin(_phase + Math.PI * 0.66);
        var wet = (Read(d1) + Read(d2)) * 0.5f;

        if (++_write >= _buffer.Length) _write = 0;
        var mix = Math.Clamp(Mix, 0f, 1f);
        return input * (1f - mix * 0.5f) + wet * mix;
    }

    private float Read(float delay)
    {
        var pos = _write - delay;
        while (pos < 0) pos += _buffer.Length;
        var i0 = (int)pos;
        var frac = pos - i0;
        var i1 = i0 + 1 >= _buffer.Length ? 0 : i0 + 1;
        return _buffer[i0] + (_buffer[i1] - _buffer[i0]) * frac;
    }
}

/// <summary>Saturation + sample-rate and bit-depth reduction (old radio, 8-bit, demon voices).</summary>
public sealed class LofiEffect
{
    private float _held;
    private int _counter;

    /// <summary>0..1 saturation amount.</summary>
    public float Drive { get; set; } = 0.3f;

    /// <summary>Bit depth, 16 = clean, 4 = very crunchy.</summary>
    public float Bits { get; set; } = 10f;

    /// <summary>Keeps one sample out of N (1 = off).</summary>
    public int Downsample { get; set; } = 2;

    public float Process(float input)
    {
        var drive = 1f + Math.Clamp(Drive, 0f, 1f) * 20f;
        var x = MathF.Tanh(input * drive) / MathF.Tanh(drive) * 0.7f;

        if (++_counter >= Math.Max(1, Downsample))
        {
            _counter = 0;
            var levels = MathF.Pow(2f, Math.Clamp(Bits, 2f, 16f) - 1f);
            _held = MathF.Round(x * levels) / levels;
        }
        return _held;
    }
}

/// <summary>Three-band tone control: low shelf (200 Hz), mid peak (1 kHz), high shelf (4 kHz).</summary>
public sealed class ThreeBandEq
{
    private readonly int _sampleRate;
    private readonly Biquad _low = new();
    private readonly Biquad _mid = new();
    private readonly Biquad _high = new();
    private float _lastLow = float.NaN, _lastMid = float.NaN, _lastHigh = float.NaN;

    public ThreeBandEq(int sampleRate) => _sampleRate = sampleRate;

    public void Configure(float lowDb, float midDb, float highDb)
    {
        if (lowDb != _lastLow) _low.SetLowShelf(_sampleRate, 200f, _lastLow = lowDb);
        if (midDb != _lastMid) _mid.SetPeaking(_sampleRate, 1000f, 0.8f, _lastMid = midDb);
        if (highDb != _lastHigh) _high.SetHighShelf(_sampleRate, 4000f, _lastHigh = highDb);
    }

    public float Process(float x) => _high.Process(_mid.Process(_low.Process(x)));
}
