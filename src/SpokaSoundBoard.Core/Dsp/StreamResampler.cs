namespace SpokaSoundBoard.Core.Dsp;

/// <summary>
/// Streaming rational resampler (polyphase windowed sinc, Blackman): any rate to any rate, e.g. the voice engine's
/// 22.05 kHz to the mixer's 48 kHz (×320/147). Keeps its history between blocks, so splitting the input in pieces
/// gives exactly the same output. Not for the audio thread (it appends to a list).
/// </summary>
public sealed class StreamResampler
{
    private readonly int _up, _down, _taps;
    private readonly float[] _coeffs; // phase-major: _coeffs[phase * _taps + k]
    private float[] _buffer;          // history (taps - 1 samples) followed by the newest input
    private long _pos;                // next output, in 1/_up input samples, from the start of _buffer

    public StreamResampler(int inputRate, int outputRate, int tapsPerPhase = 32)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        var g = Gcd(inputRate, outputRate);
        _up = outputRate / g;
        _down = inputRate / g;
        _taps = tapsPerPhase;
        InputRate = inputRate;
        OutputRate = outputRate;

        // Prototype low-pass at the up-sampled rate, cut at the lower Nyquist (95 %), gain _up.
        var length = _up * _taps;
        var cutoff = 0.5 / Math.Max(_up, _down) * 0.95;
        var mid = (length - 1) / 2.0;
        var proto = new double[length];
        for (var n = 0; n < length; n++)
        {
            var x = n - mid;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / (length - 1)) + 0.08 * Math.Cos(4 * Math.PI * n / (length - 1));
            proto[n] = sinc * w * _up;
        }
        _coeffs = new float[length];
        for (var phase = 0; phase < _up; phase++)
            for (var k = 0; k < _taps; k++)
                _coeffs[phase * _taps + k] = (float)proto[phase + k * _up];

        _buffer = new float[_taps - 1];
        _pos = (long)(_taps - 1) * _up;
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>Delay added by the filter, in input samples.</summary>
    public double Latency => (_taps - 1) / 2.0;

    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        var history = _taps - 1;
        var combined = new float[history + input.Length];
        Array.Copy(_buffer, combined, history);
        input.CopyTo(combined.AsSpan(history));

        while (true)
        {
            var j = _pos / _up;
            if (j >= combined.Length) break;
            var phase = (int)(_pos - j * _up);
            double acc = 0;
            var c = phase * _taps;
            for (var k = 0; k < _taps; k++) acc += _coeffs[c + k] * combined[j - k];
            output.Add((float)acc);
            _pos += _down;
        }

        // Keep the last taps - 1 samples; positions are now relative to them.
        var consumed = combined.Length - history;
        _buffer = combined[consumed..];
        _pos -= (long)consumed * _up;
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        _pos = (long)(_taps - 1) * _up;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
