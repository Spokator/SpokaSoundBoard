namespace SpokaSoundBoard.Core.Dsp;

/// <summary>
/// Streaming 48 kHz → 16 kHz converter for speech recognition: a windowed-sinc low-pass at 7 kHz (63 taps,
/// Blackman) then one sample out of three. Keeps its history between blocks, so the stream has no seams.
/// </summary>
public sealed class Decimator
{
    public const int Factor = 3;
    private const int Taps = 63;

    private static readonly float[] Kernel = BuildKernel();
    private readonly float[] _history = new float[Taps - 1];
    private int _phase;

    private static float[] BuildKernel()
    {
        var k = new float[Taps];
        const double cutoff = 7000.0 / 48000.0; // normalized to the input rate
        var mid = (Taps - 1) / 2.0;
        double sum = 0;
        for (var i = 0; i < Taps; i++)
        {
            var x = i - mid;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1));
            k[i] = (float)(sinc * w);
            sum += k[i];
        }
        for (var i = 0; i < Taps; i++) k[i] = (float)(k[i] / sum);
        return k;
    }

    /// <summary>Converts <paramref name="input"/> (48 kHz) and appends the 16 kHz samples to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        var h = _history.Length;
        for (var n = 0; n < input.Length; n++)
        {
            if (_phase == 0)
            {
                // y = Σ k[j] · x[n − j], with x before this block taken from the history.
                double acc = 0;
                for (var j = 0; j < Taps; j++)
                {
                    var idx = n - j;
                    acc += Kernel[j] * (idx >= 0 ? input[idx] : _history[h + idx]);
                }
                output.Add((float)acc);
            }
            _phase = (_phase + 1) % Factor;
        }

        // Keep the last Taps − 1 input samples.
        if (input.Length >= h)
        {
            input[^h..].CopyTo(_history);
        }
        else
        {
            Array.Copy(_history, input.Length, _history, 0, h - input.Length);
            input.CopyTo(_history.AsSpan(h - input.Length));
        }
    }

    public void Reset()
    {
        Array.Clear(_history);
        _phase = 0;
    }
}
