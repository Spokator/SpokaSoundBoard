using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Dsp;

/// <summary>
/// Real-time effects of the music bus (stereo interleaved, audio thread): karaoke → preamp + bass/treble shelves +
/// 10-band EQ → stereo width → 8D rotation → concert-hall reverb → soft safety limiter. The settings are read on
/// every block, every parameter is smoothed (moving a slider never clicks), biquad coefficients are only
/// recomputed while a gain is actually moving, and a neutral chain is bypassed at zero cost with a short
/// dry/processed crossfade when it switches on or off. The playback speed is not handled here (it is applied by
/// the player on each deck's source). Allocation-free in steady state.
/// </summary>
public sealed class MusicEffectsChain
{
    public const float BassFrequency = 100f;
    public const float TrebleFrequency = 8000f;
    public const float KaraokeBassFrequency = 150f;
    private const float EqQ = 1.41f; // one octave
    private const int SubBlock = 64;
    private const float LimiterThreshold = 0.8912509f; // -1 dBFS

    private readonly int _sampleRate;
    private readonly float _paramSmoothing; // per sample, ~20 ms
    private readonly float _dbSmoothing;    // per sub-block, ~40 ms
    private readonly float _bypassStep;     // per sample, ~8 ms
    private readonly float _limiterAttack, _limiterRelease;

    private readonly EqBand[] _bands;
    private readonly Biquad _karaokeLow1 = new(), _karaokeLow2 = new();
    private readonly Surround8D _surround;
    private readonly StereoReverb _reverb;

    private float[] _dry = [];
    private float _bypass;   // 0 = dry, 1 = processed
    private bool _idle = true;

    private float _preamp = 1f, _preampTarget = 1f;
    private float _karaoke, _karaokeTarget;
    private float _width = 1f, _widthTarget = 1f;
    private float _surroundAmount, _surroundTarget, _surroundRate = 0.12f;
    private float _reverbMix, _reverbTarget;
    private float _limiterGain = 1f;

    public MusicEffectsChain(int sampleRate)
    {
        _sampleRate = sampleRate;
        _paramSmoothing = 1f - MathF.Exp(-1f / (0.02f * sampleRate));
        _dbSmoothing = 1f - MathF.Exp(-SubBlock / (0.04f * sampleRate));
        _bypassStep = 1f / (0.008f * sampleRate);
        _limiterAttack = 1f - MathF.Exp(-1f / (0.001f * sampleRate));
        _limiterRelease = 1f - MathF.Exp(-1f / (0.15f * sampleRate));

        _bands = new EqBand[2 + MusicEffectSettings.EqFrequencies.Length];
        _bands[0] = new EqBand(EqKind.LowShelf, BassFrequency);
        _bands[1] = new EqBand(EqKind.HighShelf, TrebleFrequency);
        for (var i = 0; i < MusicEffectSettings.EqFrequencies.Length; i++)
            _bands[2 + i] = new EqBand(EqKind.Peaking, MusicEffectSettings.EqFrequencies[i]);

        _karaokeLow1.SetLowPass(sampleRate, KaraokeBassFrequency);
        _karaokeLow2.SetLowPass(sampleRate, KaraokeBassFrequency);
        _surround = new Surround8D(sampleRate);
        _reverb = new StereoReverb(sampleRate);
    }

    /// <summary>True while the chain is processing (or crossfading to/from bypass).</summary>
    public bool IsActive => !_idle;

    /// <summary>
    /// True when <paramref name="s"/> leaves the signal untouched, ignoring <see cref="MusicEffectSettings.Speed"/>
    /// (handled by the decks). Allocation-free, unlike <see cref="MusicEffectSettings.IsNeutral"/>.
    /// </summary>
    public static bool IsChainNeutral(MusicEffectSettings? s)
    {
        if (s is not { Enabled: true }) return true;
        if (s.BassDb != 0 || s.TrebleDb != 0 || s.Surround8D || s.Karaoke || s.ReverbMix > 0 || s.StereoWidth != 1f)
            return false;
        if (!s.EqEnabled) return true;
        return s.Eq32 == 0 && s.Eq64 == 0 && s.Eq125 == 0 && s.Eq250 == 0 && s.Eq500 == 0 && s.Eq1k == 0
               && s.Eq2k == 0 && s.Eq4k == 0 && s.Eq8k == 0 && s.Eq16k == 0;
    }

    public void Reset()
    {
        foreach (var b in _bands) b.Reset();
        _karaokeLow1.Reset();
        _karaokeLow2.Reset();
        _surround.Reset();
        _reverb.Reset();
        _limiterGain = 1f;
    }

    /// <summary>Processes a block in place. Never throws on bad settings (NaN / out-of-range values are sanitized).</summary>
    public void Process(Span<float> interleaved, MusicEffectSettings? settings)
    {
        var want = !IsChainNeutral(settings);
        if (!want && _bypass == 0f)
        {
            if (!_idle)
            {
                Reset();
                _idle = true;
            }
            return;
        }

        if (want)
        {
            ReadTargets(settings!);
            if (_idle)
            {
                // Starting from bypass: no slider ramps, the dry/processed crossfade below hides the switch.
                SnapToTargets();
                Reset();
                _idle = false;
            }
        }

        var frames = interleaved.Length / 2;
        var x = interleaved[..(frames * 2)];
        var blend = !(want && _bypass == 1f);
        if (blend)
        {
            if (_dry.Length < x.Length) _dry = new float[x.Length];
            x.CopyTo(_dry);
        }

        Preamp(x);
        Karaoke(x);
        Equalize(x, frames);
        Width(x);
        Surround(x);
        Reverb(x);
        var clean = Limit(x);

        if (blend)
        {
            var target = want ? 1f : 0f;
            for (var i = 0; i < x.Length; i += 2)
            {
                if (_bypass != target)
                    _bypass = want ? MathF.Min(1f, _bypass + _bypassStep) : MathF.Max(0f, _bypass - _bypassStep);
                var b = _bypass;
                // The soft clip keeps the switch clean even when the dry signal is hot.
                x[i] = SoftClip(_dry[i] + (x[i] - _dry[i]) * b);
                x[i + 1] = SoftClip(_dry[i + 1] + (x[i + 1] - _dry[i + 1]) * b);
            }
        }

        if (!clean) Reset();
    }

    // ---- parameters -------------------------------------------------------------------------------------------

    private static float Sane(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static float EqValue(MusicEffectSettings s, int i) => i switch
    {
        0 => s.Eq32, 1 => s.Eq64, 2 => s.Eq125, 3 => s.Eq250, 4 => s.Eq500,
        5 => s.Eq1k, 6 => s.Eq2k, 7 => s.Eq4k, 8 => s.Eq8k, _ => s.Eq16k,
    };

    private void ReadTargets(MusicEffectSettings s)
    {
        _bands[0].Target = Sane(s.BassDb, -15f, 15f, 0f);
        _bands[1].Target = Sane(s.TrebleDb, -12f, 12f, 0f);
        var eq = s.EqEnabled;
        for (var i = 2; i < _bands.Length; i++)
            _bands[i].Target = eq ? Sane(EqValue(s, i - 2), -12f, 12f, 0f) : 0f;

        // Automatic preamp: half of the largest boost is compensated, the limiter catches the rest.
        var maxBoost = 0f;
        foreach (var b in _bands) maxBoost = MathF.Max(maxBoost, b.Target);
        _preampTarget = DspUtil.DbToLinear(-0.5f * maxBoost);

        _karaokeTarget = s.Karaoke ? 1f : 0f;
        _widthTarget = Sane(s.StereoWidth, 0f, 2f, 1f);
        _surroundTarget = s.Surround8D ? 1f : 0f;
        _surroundRate = Sane(s.Surround8DRate, 0.02f, 2f, 0.12f);
        _reverbTarget = Sane(s.ReverbMix, 0f, 1f, 0f);
    }

    private void SnapToTargets()
    {
        foreach (var b in _bands)
        {
            b.Current = b.Target;
            b.Dirty = true;
        }
        _preamp = _preampTarget;
        _karaoke = _karaokeTarget;
        _width = _widthTarget;
        _surroundAmount = _surroundTarget;
        _reverbMix = _reverbTarget;
    }

    private float Smooth(ref float value, float target)
    {
        if (value != target)
        {
            value += (target - value) * _paramSmoothing;
            if (MathF.Abs(target - value) < 1e-5f) value = target;
        }
        return value;
    }

    // ---- stages -----------------------------------------------------------------------------------------------

    private void Preamp(Span<float> x)
    {
        if (_preamp == 1f && _preampTarget == 1f) return;
        for (var i = 0; i < x.Length; i += 2)
        {
            var g = Smooth(ref _preamp, _preampTarget);
            x[i] *= g;
            x[i + 1] *= g;
        }
    }

    /// <summary>Vocal removal: the centre (mid) is removed above ~150 Hz, the side and the bass are kept.</summary>
    private void Karaoke(Span<float> x)
    {
        if (_karaoke == 0f && _karaokeTarget == 0f) return;
        if (_karaoke == 0f)
        {
            _karaokeLow1.Reset();
            _karaokeLow2.Reset();
        }
        for (var i = 0; i < x.Length; i += 2)
        {
            var k = Smooth(ref _karaoke, _karaokeTarget);
            var l = x[i];
            var r = x[i + 1];
            var mid = (l + r) * 0.5f;
            var side = (l - r) * 0.5f;
            var bass = _karaokeLow2.Process(_karaokeLow1.Process(mid)); // Linkwitz-Riley 24 dB/oct
            x[i] = l + (side + bass - l) * k;
            x[i + 1] = r + (bass - side - r) * k;
        }
    }

    private void Equalize(Span<float> x, int frames)
    {
        for (var start = 0; start < frames; start += SubBlock)
        {
            var n = Math.Min(SubBlock, frames - start);
            foreach (var band in _bands)
            {
                if (band.Current != band.Target)
                {
                    band.Current += (band.Target - band.Current) * _dbSmoothing;
                    if (MathF.Abs(band.Target - band.Current) < 0.01f) band.Current = band.Target;
                    band.Dirty = true;
                }
                if (band.Current == 0f && band.Target == 0f)
                {
                    band.Active = false;
                    continue;
                }
                if (!band.Active)
                {
                    band.Reset();
                    band.Active = true;
                    band.Dirty = true;
                }
                if (band.Dirty) band.Update(_sampleRate);

                var end = (start + n) * 2;
                for (var i = start * 2; i < end; i += 2)
                {
                    x[i] = band.Left.Process(x[i]);
                    x[i + 1] = band.Right.Process(x[i + 1]);
                }
            }
        }
    }

    /// <summary>Mid/side width: 0 = mono, 1 = unchanged, 2 = very wide.</summary>
    private void Width(Span<float> x)
    {
        if (_width == 1f && _widthTarget == 1f) return;
        for (var i = 0; i < x.Length; i += 2)
        {
            var w = Smooth(ref _width, _widthTarget);
            var mid = (x[i] + x[i + 1]) * 0.5f;
            var side = (x[i] - x[i + 1]) * 0.5f * w;
            x[i] = mid + side;
            x[i + 1] = mid - side;
        }
    }

    private void Surround(Span<float> x)
    {
        if (_surroundAmount == 0f && _surroundTarget == 0f) return;
        if (_surroundAmount == 0f) _surround.Reset();
        var increment = 2f * MathF.PI * _surroundRate / _sampleRate;
        for (var i = 0; i < x.Length; i += 2)
        {
            var amount = Smooth(ref _surroundAmount, _surroundTarget);
            _surround.Process(ref x[i], ref x[i + 1], amount, increment);
        }
    }

    private void Reverb(Span<float> x)
    {
        if (_reverbMix == 0f && _reverbTarget == 0f) return;
        if (_reverbMix == 0f) _reverb.Reset();
        for (var i = 0; i < x.Length; i += 2)
        {
            var mix = Smooth(ref _reverbMix, _reverbTarget);
            _reverb.Process(x[i], x[i + 1], out var wl, out var wr);
            var dry = 1f - 0.35f * mix;
            x[i] = x[i] * dry + wl * mix;
            x[i + 1] = x[i + 1] * dry + wr * mix;
        }
    }

    /// <summary>
    /// Stereo-linked gain-riding limiter at -1 dBFS (1 ms attack, 150 ms release) followed by a tanh soft knee so
    /// the attack overshoot never hard-clips. Returns false when a non-finite sample had to be discarded.
    /// </summary>
    private bool Limit(Span<float> x)
    {
        var clean = true;
        for (var i = 0; i < x.Length; i += 2)
        {
            var l = x[i];
            var r = x[i + 1];
            if (!float.IsFinite(l) || !float.IsFinite(r))
            {
                l = r = 0f;
                clean = false;
            }
            var peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            var target = peak > LimiterThreshold ? LimiterThreshold / peak : 1f;
            _limiterGain += (target - _limiterGain) * (target < _limiterGain ? _limiterAttack : _limiterRelease);
            x[i] = SoftClip(l * _limiterGain);
            x[i + 1] = SoftClip(r * _limiterGain);
        }
        return clean;
    }

    internal static float SoftClip(float x)
    {
        var a = MathF.Abs(x);
        if (a <= LimiterThreshold) return x;
        const float knee = 1f - LimiterThreshold;
        var y = LimiterThreshold + knee * MathF.Tanh((a - LimiterThreshold) / knee);
        return MathF.CopySign(MathF.Min(y, 1f), x);
    }

    // ---- building blocks --------------------------------------------------------------------------------------

    private enum EqKind
    {
        LowShelf,
        HighShelf,
        Peaking,
    }

    private sealed class EqBand(EqKind kind, float frequency)
    {
        public readonly Biquad Left = new(), Right = new();
        public float Current, Target;
        public bool Active, Dirty = true;

        public void Reset()
        {
            Left.Reset();
            Right.Reset();
        }

        public void Update(int sampleRate)
        {
            switch (kind)
            {
                case EqKind.LowShelf:
                    Left.SetLowShelf(sampleRate, frequency, Current);
                    Right.SetLowShelf(sampleRate, frequency, Current);
                    break;
                case EqKind.HighShelf:
                    Left.SetHighShelf(sampleRate, frequency, Current);
                    Right.SetHighShelf(sampleRate, frequency, Current);
                    break;
                default:
                    Left.SetPeaking(sampleRate, frequency, EqQ, Current);
                    Right.SetPeaking(sampleRate, frequency, EqQ, Current);
                    break;
            }
            Dirty = false;
        }
    }

    /// <summary>
    /// "8D audio": the (mid) signal slowly orbits the listener. Equal-power panning plus a simple head model — the
    /// far ear hears the sound slightly later (interaural delay, up to ~0.6 ms) and darker (head shadow), and the
    /// sound gets a little duller and quieter while it passes behind.
    /// </summary>
    private sealed class Surround8D
    {
        private const int Size = 64; // power of two, > max delay
        private readonly float[] _delay = new float[Size];
        private readonly float _maxDelay;
        private int _write;
        private float _phase;
        private float _lowL, _lowR;

        public Surround8D(int sampleRate) => _maxDelay = MathF.Min(Size - 2, 0.00065f * sampleRate);

        public void Reset()
        {
            Array.Clear(_delay);
            _write = 0;
            _lowL = _lowR = 0f;
            // The phase is kept: re-enabling continues the orbit where it was.
        }

        public void Process(ref float left, ref float right, float amount, float increment)
        {
            _phase += increment;
            if (_phase >= 2f * MathF.PI) _phase -= 2f * MathF.PI;
            var s = MathF.Sin(_phase); // -1 = left, +1 = right
            var c = MathF.Cos(_phase); // +1 = front, -1 = behind

            var mid = (left + right) * 0.5f;
            var side = (left - right) * 0.5f;
            _delay[_write] = mid;

            var angle = (0.85f * s + 1f) * (MathF.PI / 4f);
            var gainL = MathF.Cos(angle);
            var gainR = MathF.Sin(angle);
            var farL = MathF.Max(0f, s);
            var farR = MathF.Max(0f, -s);
            var behind = MathF.Max(0f, -c);

            var inL = ReadDelay(farL * _maxDelay);
            var inR = ReadDelay(farR * _maxDelay);
            var aL = MathF.Min(0.85f, 0.6f * farL + 0.3f * behind);
            var aR = MathF.Min(0.85f, 0.6f * farR + 0.3f * behind);
            _lowL = inL + (_lowL - inL) * aL;
            _lowR = inR + (_lowR - inR) * aR;
            if (MathF.Abs(_lowL) < 1e-20f) _lowL = 0f;
            if (MathF.Abs(_lowR) < 1e-20f) _lowR = 0f;

            var level = 1.3f * (1f - 0.15f * behind);
            var outL = _lowL * gainL * level + side * 0.3f;
            var outR = _lowR * gainR * level - side * 0.3f;
            left += (outL - left) * amount;
            right += (outR - right) * amount;

            _write = (_write + 1) & (Size - 1);
        }

        private float ReadDelay(float delay)
        {
            var pos = _write - delay;
            var i0 = (int)MathF.Floor(pos);
            var t = pos - i0;
            var a = _delay[i0 & (Size - 1)];
            var b = _delay[(i0 + 1) & (Size - 1)];
            return a + (b - a) * t;
        }
    }
}
