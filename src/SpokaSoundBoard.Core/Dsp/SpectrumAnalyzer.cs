namespace SpokaSoundBoard.Core.Dsp;

/// <summary>
/// Spectrum for a music visualizer. The audio thread pushes its output with <see cref="Write"/> (lock-free,
/// allocation-free ring of the last <see cref="Size"/> mono frames); the UI thread calls <see cref="GetBands"/> to
/// get log-spaced band levels (0..1, smoothed) computed by a Hann-windowed FFT.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int Size = 2048;
    private const int Mask = Size - 1;

    private readonly float[] _ring = new float[Size];
    private int _write;

    // UI-side state (guarded by _readLock).
    private readonly object _readLock = new();
    private readonly float[] _re = new float[Size];
    private readonly float[] _im = new float[Size];
    private readonly float[] _window = new float[Size];
    private readonly float[] _magnitude = new float[Size / 2 + 1];
    private readonly float[] _cos = new float[Size / 2];
    private readonly float[] _sin = new float[Size / 2];
    private readonly int[] _bitReverse = new int[Size];
    private float[] _smoothed = [];

    public SpectrumAnalyzer(int sampleRate)
    {
        SampleRate = sampleRate;
        for (var i = 0; i < Size; i++)
            _window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (Size - 1));
        for (var i = 0; i < Size / 2; i++)
        {
            _cos[i] = MathF.Cos(2f * MathF.PI * i / Size);
            _sin[i] = MathF.Sin(2f * MathF.PI * i / Size);
        }
        var bits = (int)Math.Log2(Size);
        for (var i = 0; i < Size; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _bitReverse[i] = r;
        }
    }

    public int SampleRate { get; }

    /// <summary>Lowest band edge in Hz.</summary>
    public float MinFrequency { get; set; } = 30f;

    /// <summary>Highest band edge in Hz.</summary>
    public float MaxFrequency { get; set; } = 16000f;

    /// <summary>Level shown as 0 (a full-scale sine is 0 dB = 1).</summary>
    public float FloorDb { get; set; } = -60f;

    /// <summary>Per-call fall-back factor of the bars (0 = no smoothing, closer to 1 = slower fall).</summary>
    public float Decay { get; set; } = 0.8f;

    /// <summary>Audio thread: appends a block of stereo interleaved output (mixed to mono).</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        var start = Math.Max(0, frames - Size);
        var w = _write;
        for (var f = start; f < frames; f++)
        {
            _ring[w] = (interleaved[f * 2] + interleaved[f * 2 + 1]) * 0.5f;
            w = (w + 1) & Mask;
        }
        Volatile.Write(ref _write, w);
    }

    /// <summary>Forgets the captured audio and the bar smoothing.</summary>
    public void Clear()
    {
        Array.Clear(_ring);
        lock (_readLock) Array.Clear(_smoothed);
    }

    /// <summary>
    /// UI thread: fills <paramref name="bands"/> with log-spaced band levels between <see cref="MinFrequency"/> and
    /// <see cref="MaxFrequency"/>, each 0..1 (dB scale above <see cref="FloorDb"/>), fast attack and smooth fall.
    /// </summary>
    public void GetBands(Span<float> bands)
    {
        if (bands.IsEmpty) return;
        lock (_readLock)
        {
            var w = Volatile.Read(ref _write);
            for (var i = 0; i < Size; i++)
            {
                var j = _bitReverse[i];
                _re[j] = _ring[(w + i) & Mask] * _window[i];
                _im[j] = 0f;
            }
            Fft();

            // Hann coherent gain is 0.5: a full-scale sine peaks at Size / 4.
            const float norm = 4f / Size;
            for (var k = 0; k <= Size / 2; k++)
                _magnitude[k] = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * norm;

            if (_smoothed.Length != bands.Length) _smoothed = new float[bands.Length];

            var binHz = (float)SampleRate / Size;
            var nyquist = SampleRate / 2f;
            var min = Math.Clamp(MinFrequency, binHz, nyquist);
            var max = Math.Clamp(MaxFrequency, min * 1.01f, nyquist);
            var ratio = max / min;
            var floor = MathF.Min(-1f, FloorDb);
            var decay = Math.Clamp(Decay, 0f, 0.99f);

            for (var b = 0; b < bands.Length; b++)
            {
                var lo = min * MathF.Pow(ratio, (float)b / bands.Length) / binHz;
                var hi = min * MathF.Pow(ratio, (float)(b + 1) / bands.Length) / binHz;
                float m;
                if (hi - lo < 1f)
                {
                    var c = Math.Clamp((lo + hi) * 0.5f, 0f, Size / 2 - 1);
                    var i0 = (int)c;
                    var t = c - i0;
                    m = _magnitude[i0] * (1 - t) + _magnitude[i0 + 1] * t;
                }
                else
                {
                    m = 0f;
                    var k0 = Math.Clamp((int)MathF.Ceiling(lo), 0, Size / 2);
                    var k1 = Math.Clamp((int)MathF.Floor(hi), k0, Size / 2);
                    for (var k = k0; k <= k1; k++) m = MathF.Max(m, _magnitude[k]);
                }

                var db = 20f * MathF.Log10(m + 1e-9f);
                var level = Math.Clamp((db - floor) / -floor, 0f, 1f);
                var s = _smoothed[b];
                s = level > s ? s + (level - s) * 0.7f : s * decay + level * (1f - decay);
                if (!float.IsFinite(s)) s = 0f;
                _smoothed[b] = s;
                bands[b] = s;
            }
        }
    }

    /// <summary>Convenience overload for the UI (allocates the result array).</summary>
    public float[] GetBands(int count)
    {
        var result = new float[Math.Max(0, count)];
        GetBands(result);
        return result;
    }

    /// <summary>In-place iterative radix-2 FFT on bit-reversed input.</summary>
    private void Fft()
    {
        for (var size = 2; size <= Size; size <<= 1)
        {
            var half = size >> 1;
            var step = Size / size;
            for (var i = 0; i < Size; i += size)
            {
                for (var j = 0; j < half; j++)
                {
                    var k = j * step;
                    var wr = _cos[k];
                    var wi = -_sin[k];
                    var a = i + j;
                    var b = a + half;
                    var tr = wr * _re[b] - wi * _im[b];
                    var ti = wr * _im[b] + wi * _re[b];
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
            }
        }
    }
}
