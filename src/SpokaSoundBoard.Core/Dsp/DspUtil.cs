namespace SpokaSoundBoard.Core.Dsp;

public static class DspUtil
{
    public static float DbToLinear(float db) => db <= -96f ? 0f : MathF.Pow(10f, db / 20f);

    public static float LinearToDb(float linear) => linear <= 1e-6f ? -120f : 20f * MathF.Log10(linear);

    /// <summary>One-pole smoothing coefficient for the given time constant.</summary>
    public static float TimeCoefficient(float milliseconds, float sampleRate)
    {
        if (milliseconds <= 0.01f) return 0f;
        return MathF.Exp(-1f / (milliseconds * 0.001f * sampleRate));
    }
}

/// <summary>Fast, allocation-free white noise generator.</summary>
public struct NoiseGenerator
{
    private uint _state;

    public NoiseGenerator(uint seed) => _state = seed == 0 ? 0x1234567u : seed;

    public float Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return (_state / (float)uint.MaxValue) * 2f - 1f;
    }
}
