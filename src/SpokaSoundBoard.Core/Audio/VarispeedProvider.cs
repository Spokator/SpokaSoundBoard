using NAudio.Wave;
using SpokaSoundBoard.Core.Dsp;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Fractional-rate resampler (4-point Hermite interpolation) used for varispeed playback: speed 2 plays twice as
/// fast and one octave higher, speed 0.8 slower and lower ("vaporwave"). Tracks the exact source position so the
/// player can keep reporting file time. At speed 1 with an integer phase, samples are copied unchanged
/// (bit-exact passthrough, zero interpolation cost). Never throws from <see cref="Read"/>: a failing source is
/// treated as the end of the stream (see <see cref="Error"/>).
/// </summary>
public sealed class VarispeedProvider : ISampleProvider
{
    public const float MinSpeed = 0.5f;
    public const float MaxSpeed = 2f;

    /// <summary>Source frames buffered ahead of the read position.</summary>
    private const int Capacity = 4096;

    private readonly ISampleProvider _source;
    private readonly float[] _buf = new float[Capacity * 2];
    private readonly float _speedSmoothing;
    private readonly AntiAliasFilter _antiAlias;

    // _buf holds source frames [_base, _base + _count). Frame _idx (+ _frac) is the read position; the frame
    // before it is always kept as interpolation history.
    private int _count;
    private int _idx;
    private double _frac;
    private long _base;
    private bool _eof;
    private bool _fresh;
    private float _speed = 1f;
    private float _fillSpeed = 1f;

    public VarispeedProvider(ISampleProvider source)
    {
        if (source.WaveFormat.Channels != 2) throw new ArgumentException("Stereo source expected.", nameof(source));
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        _speedSmoothing = 1f - MathF.Exp(-1f / (0.03f * source.WaveFormat.SampleRate));
        _antiAlias = new AntiAliasFilter(source.WaveFormat.SampleRate);
        Reset(0);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Target speed (clamped to 0.5..2). Changes are smoothed over ~30 ms so they never click.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>Exact read position in source frames (file time × sample rate).</summary>
    public double SourcePosition => _base + _idx + _frac;

    /// <summary>True once every source frame has been played.</summary>
    public bool IsEnded => _eof && _idx >= _count;

    /// <summary>The exception thrown by the source, if reading stopped because of an error.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Forgets the buffered audio after the source has been repositioned to <paramref name="frame"/>.</summary>
    public void Reset(long frame)
    {
        _buf[0] = _buf[1] = 0f;
        _count = 1;
        _idx = 1;
        _frac = 0;
        _base = frame - 1;
        _eof = false;
        _fresh = true;
        Error = null;
        _antiAlias.Reset();
    }

    /// <summary>Marks the stream as finished (used when the owner detects an unrecoverable error).</summary>
    public void MarkEnded(Exception? error = null)
    {
        _eof = true;
        _idx = _count;
        Error ??= error;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / 2;
        var target = float.IsFinite(Speed) ? Math.Clamp(Speed, MinSpeed, MaxSpeed) : 1f;
        if (_fresh) _speed = target; // a freshly (re)started stream starts directly at the right speed
        _fillSpeed = target;

        var done = 0;
        while (done < frames)
        {
            if (_speed == 1f && target == 1f && _frac == 0.0)
            {
                // Passthrough: straight copy.
                var avail = _count - _idx;
                if (avail <= 0)
                {
                    if (_eof) break;
                    Fill();
                    continue;
                }
                var n = Math.Min(avail, frames - done);
                Array.Copy(_buf, _idx * 2, buffer, offset + done * 2, n * 2);
                _idx += n;
                done += n;
                continue;
            }

            if (_idx >= _count)
            {
                if (_eof) break;
                Fill();
                continue;
            }
            if (_idx + 2 >= _count && !_eof)
            {
                Fill();
                continue;
            }

            var limit = _eof ? _count : _count - 2;
            while (done < frames && _idx < limit)
            {
                if (_speed != target)
                {
                    _speed += (target - _speed) * _speedSmoothing;
                    if (MathF.Abs(target - _speed) < 1e-4f) _speed = target;
                }

                var t = (float)_frac;
                var j = _idx * 2;
                var has1 = _idx + 1 < _count;
                var has2 = _idx + 2 < _count;
                var o = offset + done * 2;
                for (var ch = 0; ch < 2; ch++)
                {
                    var xm1 = _buf[j - 2 + ch];
                    var x0 = _buf[j + ch];
                    var x1 = has1 ? _buf[j + 2 + ch] : 0f;
                    var x2 = has2 ? _buf[j + 4 + ch] : 0f;
                    buffer[o + ch] = Hermite(xm1, x0, x1, x2, t);
                }
                done++;

                _frac += _speed;
                var step = (int)_frac;
                _idx += step;
                _frac -= step;
                if (_speed == 1f && target == 1f && _frac == 0.0) break;
            }
        }

        if (done > 0) _fresh = false;
        return done * 2;
    }

    /// <summary>4-point, 3rd-order Hermite (Catmull-Rom). Returns x0 exactly when t = 0.</summary>
    private static float Hermite(float xm1, float x0, float x1, float x2, float t)
    {
        var c1 = 0.5f * (x1 - xm1);
        var c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
        var c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }

    private void Fill()
    {
        // Compact: keep one frame of history before the read position.
        var keep = _idx - 1;
        if (keep > 0)
        {
            var remaining = Math.Max(0, _count - keep);
            if (remaining > 0) Array.Copy(_buf, keep * 2, _buf, 0, remaining * 2);
            _count = remaining;
            _idx -= keep;
            _base += keep;
        }

        var space = Capacity - _count;
        if (space <= 0) return;
        try
        {
            var n = _source.Read(_buf, _count * 2, space * 2);
            if (n < 2)
            {
                _eof = true;
                return;
            }
            var got = n / 2;
            _antiAlias.Process(_buf.AsSpan(_count * 2, got * 2), _fillSpeed);
            _count += got;
        }
        catch (Exception ex)
        {
            // Corrupted / unreadable file: treat as the end of the track.
            Error = ex;
            _eof = true;
        }
    }

    /// <summary>
    /// 4th-order Butterworth low-pass applied to the source before speeding it up (limits aliasing). Faded in/out
    /// so toggling it never clicks; completely bypassed at speed ≤ 1.
    /// </summary>
    private sealed class AntiAliasFilter(int sampleRate)
    {
        private const float Q1 = 0.5412f, Q2 = 1.3066f;
        private readonly Biquad _l1 = new(), _l2 = new(), _r1 = new(), _r2 = new();
        private readonly float _mixStep = 1f / 256f;
        private float _mix;
        private bool _running;
        private float _designedFor;

        public void Reset()
        {
            _l1.Reset(); _l2.Reset(); _r1.Reset(); _r2.Reset();
            _mix = 0f;
            _running = false;
        }

        public void Process(Span<float> interleaved, float speed)
        {
            var want = speed > 1.01f;
            if (!want && _mix == 0f)
            {
                _running = false;
                return;
            }

            if (want && (!_running || MathF.Abs(speed - _designedFor) > 0.005f))
            {
                var cutoff = MathF.Min(0.45f * sampleRate / speed, 0.45f * sampleRate);
                _l1.SetLowPass(sampleRate, cutoff, Q1); _r1.SetLowPass(sampleRate, cutoff, Q1);
                _l2.SetLowPass(sampleRate, cutoff, Q2); _r2.SetLowPass(sampleRate, cutoff, Q2);
                if (!_running) { _l1.Reset(); _l2.Reset(); _r1.Reset(); _r2.Reset(); }
                _running = true;
                _designedFor = speed;
            }

            var target = want ? 1f : 0f;
            for (var i = 0; i + 1 < interleaved.Length; i += 2)
            {
                if (_mix != target) _mix = want ? MathF.Min(1f, _mix + _mixStep) : MathF.Max(0f, _mix - _mixStep);
                var l = interleaved[i];
                var r = interleaved[i + 1];
                var fl = _l2.Process(_l1.Process(l));
                var fr = _r2.Process(_r1.Process(r));
                interleaved[i] = l + (fl - l) * _mix;
                interleaved[i + 1] = r + (fr - r) * _mix;
            }
        }
    }
}
