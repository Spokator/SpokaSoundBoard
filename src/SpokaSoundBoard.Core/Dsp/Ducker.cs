namespace SpokaSoundBoard.Core.Dsp;

/// <summary>Smoothly lowers the media bus while the voice is active ("ducking").</summary>
public sealed class Ducker
{
    private readonly int _sampleRate;
    private float _gain = 1f;
    private float _attackMs = -1, _releaseMs = -1;
    private float _attackCoef, _releaseCoef;

    public Ducker(int sampleRate) => _sampleRate = sampleRate;

    public float Gain => _gain;

    public void Configure(float attackMs, float releaseMs)
    {
        if (attackMs != _attackMs)
        {
            _attackMs = attackMs;
            _attackCoef = DspUtil.TimeCoefficient(attackMs, _sampleRate);
        }
        if (releaseMs != _releaseMs)
        {
            _releaseMs = releaseMs;
            _releaseCoef = DspUtil.TimeCoefficient(releaseMs, _sampleRate);
        }
    }

    /// <summary>Advances one sample and returns the gain to apply to the media.</summary>
    public float Next(bool voiceActive, float duckedGain)
    {
        var target = voiceActive ? duckedGain : 1f;
        var coef = target < _gain ? _attackCoef : _releaseCoef;
        _gain = coef * _gain + (1 - coef) * target;
        return _gain;
    }
}
