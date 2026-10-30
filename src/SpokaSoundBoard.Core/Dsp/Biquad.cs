namespace SpokaSoundBoard.Core.Dsp;

/// <summary>RBJ biquad filter (transposed direct form II).</summary>
public sealed class Biquad
{
    private float _b0 = 1, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    public void SetLowPass(float sampleRate, float frequency, float q = 0.7071f)
    {
        Compute(sampleRate, frequency, q, out var w0, out var cos, out var alpha);
        var b1 = 1 - cos;
        Set(b1 / 2, b1, b1 / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void SetHighPass(float sampleRate, float frequency, float q = 0.7071f)
    {
        Compute(sampleRate, frequency, q, out var w0, out var cos, out var alpha);
        var b1 = -(1 + cos);
        Set(-b1 / 2, b1, -b1 / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void SetPeaking(float sampleRate, float frequency, float q, float gainDb)
    {
        Compute(sampleRate, frequency, q, out var w0, out var cos, out var alpha);
        var a = MathF.Pow(10f, gainDb / 40f);
        Set(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
    }

    public void Reset() => _z1 = _z2 = 0;

    public float Process(float x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        // Flush denormals / recover from any instability.
        if (!float.IsFinite(y)) { Reset(); return 0; }
        return y;
    }

    private static void Compute(float sampleRate, float frequency, float q, out float w0, out float cos, out float alpha)
    {
        frequency = Math.Clamp(frequency, 10f, sampleRate * 0.45f);
        w0 = 2 * MathF.PI * frequency / sampleRate;
        cos = MathF.Cos(w0);
        alpha = MathF.Sin(w0) / (2 * MathF.Max(q, 0.05f));
    }

    private void Set(float b0, float b1, float b2, float a0, float a1, float a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }
}
