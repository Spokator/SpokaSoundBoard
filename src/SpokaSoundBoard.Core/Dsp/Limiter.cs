namespace SpokaSoundBoard.Core.Dsp;

/// <summary>Stereo-linked peak limiter used on every output bus to prevent clipping.</summary>
public sealed class Limiter
{
    private readonly float _release;
    private float _gain = 1f;

    public Limiter(int sampleRate, float ceilingDb = -1f, float releaseMs = 120f)
    {
        Ceiling = DspUtil.DbToLinear(ceilingDb);
        _release = DspUtil.TimeCoefficient(releaseMs, sampleRate);
    }

    public float Ceiling { get; }

    /// <summary>Current gain reduction (1 = none).</summary>
    public float Gain => _gain;

    public void ProcessStereo(Span<float> interleaved)
    {
        for (var i = 0; i + 1 < interleaved.Length; i += 2)
        {
            var l = interleaved[i];
            var r = interleaved[i + 1];
            var peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            var needed = peak * _gain > Ceiling ? Ceiling / peak : 1f;
            if (needed < _gain) _gain = needed;
            else _gain = _release * _gain + (1 - _release) * 1f;

            interleaved[i] = Math.Clamp(l * _gain, -1f, 1f);
            interleaved[i + 1] = Math.Clamp(r * _gain, -1f, 1f);
        }
    }
}
