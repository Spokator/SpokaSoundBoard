namespace SpokaSoundBoard.Core.Dsp;

/// <summary>Feed-forward peak compressor with automatic make-up gain.</summary>
public sealed class Compressor
{
    private readonly int _sampleRate;
    private float _envelope;
    private float _attack;
    private float _release;

    public Compressor(int sampleRate)
    {
        _sampleRate = sampleRate;
        SetTimes(5f, 120f);
    }

    public float ThresholdDb { get; set; } = -24f;
    public float Ratio { get; set; } = 3f;
    public float MakeupDb { get; set; } = 6f;

    public void SetTimes(float attackMs, float releaseMs)
    {
        _attack = DspUtil.TimeCoefficient(attackMs, _sampleRate);
        _release = DspUtil.TimeCoefficient(releaseMs, _sampleRate);
    }

    /// <summary>Maps a single 0..1 "amount" knob to threshold / ratio / make-up.</summary>
    public void SetAmount(float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        ThresholdDb = -10f - amount * 25f;
        Ratio = 1.5f + amount * 6f;
        MakeupDb = -ThresholdDb * (1f - 1f / Ratio) * 0.5f;
    }

    public void Reset() => _envelope = 0;

    public float Process(float input)
    {
        var level = MathF.Abs(input);
        var coef = level > _envelope ? _attack : _release;
        _envelope = coef * _envelope + (1 - coef) * level;

        var levelDb = DspUtil.LinearToDb(_envelope);
        var gainDb = MakeupDb;
        if (levelDb > ThresholdDb)
            gainDb -= (levelDb - ThresholdDb) * (1f - 1f / MathF.Max(1f, Ratio));
        return input * DspUtil.DbToLinear(gainDb);
    }
}
