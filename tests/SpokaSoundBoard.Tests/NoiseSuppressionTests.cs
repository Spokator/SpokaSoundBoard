using System.Diagnostics;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;
using Xunit.Abstractions;

namespace SpokaSoundBoard.Tests;

/// <summary>2.11: microphone noise suppression (RNNoise).</summary>
public sealed class NoiseSuppressionTests(ITestOutputHelper output)
{
    private const int Sr = 48000;

    private static float[] Noise(int samples, float level, int seed = 7)
    {
        var rng = new Random(seed);
        var x = new float[samples];
        for (var i = 0; i < x.Length; i++) x[i] = level * (float)(rng.NextDouble() * 2 - 1);
        return x;
    }

    private static float Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var s in x) sum += s * s;
        return (float)Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    [Fact]
    public void TheLibraryIsShippedAndLoads()
    {
        using var denoiser = new NoiseSuppressor(Sr);
        Assert.True(denoiser.IsAvailable, "rnnoise.dll must be next to the app");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "RNNOISE-LICENSE.txt")));
    }

    [Fact]
    public void BackgroundNoise_IsRemoved()
    {
        using var denoiser = new NoiseSuppressor(Sr);
        var x = Noise(Sr * 3, 0.05f);
        var before = Rms(x.AsSpan(Sr * 2));
        // Blocks of 441 samples: not aligned on the 480-sample frames on purpose.
        for (var i = 0; i < x.Length; i += 441) denoiser.Process(x.AsSpan(i, Math.Min(441, x.Length - i)), true, 1f);
        var after = Rms(x.AsSpan(Sr * 2));
        output.WriteLine($"bruit seul : {20 * Math.Log10(before):0.0} dBFS → {20 * Math.Log10(Math.Max(after, 1e-9)):0.0} dBFS");
        Assert.True(after < before / 4, "at least 12 dB less noise");
        Assert.InRange(denoiser.VoiceProbability, 0f, 0.5f);
    }

    [Fact]
    public void Off_LeavesTheSamplesUntouched_AndOnDelaysBy30Ms()
    {
        using var denoiser = new NoiseSuppressor(Sr);
        var x = Noise(4800, 0.1f);
        var copy = (float[])x.Clone();
        denoiser.Process(x, false, 1f);
        Assert.Equal(copy, x);
        Assert.False(denoiser.IsRunning);

        // Amount ≈ 0 while on: the original voice, 30 ms later (the delay of the denoised voice).
        Assert.Equal(1440, NoiseSuppressor.LatencySamples);
        var y = Noise(Sr, 0.1f, 3);
        var original = (float[])y.Clone();
        denoiser.Process(y, true, 0.0001f);
        for (var i = NoiseSuppressor.LatencySamples + 2000; i < NoiseSuppressor.LatencySamples + 2100; i++)
            Assert.Equal(original[i - NoiseSuppressor.LatencySamples], y[i], 2);

        // Switched off: fades out, then leaves the path (no more delay).
        var z = Noise(Sr, 0.1f, 4);
        denoiser.Process(z, false, 1f);
        Assert.False(denoiser.IsRunning);
    }

    [Fact]
    public void TheDenoisedVoice_IsAlignedWithTheOriginal()
    {
        // A voice-like signal (harmonics with a moving pitch, in syllables): the denoised output must line up with the
        // input exactly LatencySamples later, the same delay as the original used for partial amounts.
        var x = new float[Sr * 3];
        double phase = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var f0 = 140 + 30 * Math.Sin(2 * Math.PI * 0.7 * i / Sr);
            phase += 2 * Math.PI * f0 / Sr;
            var syllable = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * i / Sr);
            x[i] = (float)(0.2 * syllable * (Math.Sin(phase) + 0.5 * Math.Sin(2 * phase) + 0.25 * Math.Sin(3 * phase)));
        }
        var y = (float[])x.Clone();
        using var denoiser = new NoiseSuppressor(Sr);
        for (var i = 0; i < y.Length; i += 512) denoiser.Process(y.AsSpan(i, Math.Min(512, y.Length - i)), true, 1f);

        var best = 0;
        var bestC = double.MinValue;
        for (var lag = 0; lag <= 3000; lag++)
        {
            double c = 0;
            for (var i = Sr; i < Sr * 2; i += 3) c += x[i] * y[i + lag];
            if (c > bestC) { bestC = c; best = lag; }
        }
        output.WriteLine($"retard mesuré : {best} échantillons");
        // RNNoise's per-band filtering shifts the phase slightly (a few samples on such a regular signal; 1 439 measured
        // on a real voice): the structural delay is three frames.
        Assert.InRange(best, NoiseSuppressor.LatencySamples - 10, NoiseSuppressor.LatencySamples + 10);
    }

    [Fact]
    public void CostsLittleCpu()
    {
        using var denoiser = new NoiseSuppressor(Sr);
        var x = Noise(Sr * 10, 0.05f);
        denoiser.Process(x.AsSpan(0, Sr), true, 1f); // warm-up
        var watch = Stopwatch.StartNew();
        for (var i = Sr; i < x.Length; i += 480) denoiser.Process(x.AsSpan(i, 480), true, 1f);
        watch.Stop();
        var perFrame = watch.Elapsed.TotalMilliseconds / ((x.Length - Sr) / 480.0);
        output.WriteLine($"RNNoise : {perFrame:0.000} ms par trame de 10 ms ({perFrame * 10:0.0} % d'un cœur)");
        Assert.True(perFrame < 2.0, "well under the 10 ms of audio each frame holds");
    }

    [Fact]
    public void Mixer_SendsTheCleanedVoice()
    {
        float BusRms(bool suppression)
        {
            var mix = new MixSettings { NoiseSuppressionEnabled = suppression, MonitorVolume = 1f, ListenToSelf = true };
            using var music = new MusicPlayer(Sr);
            using var pads = new SoundPadPlayer(Sr);
            var core = new MixerCore(new VoiceEffectSettings { GateEnabled = false, CompressorEnabled = false }, mix, music, pads);
            core.SetMicSource(new NoiseSource(0.05f));
            var main = new float[960];
            var monitor = new float[960];
            double sum = 0;
            for (var block = 0; block < 300; block++)
            {
                core.Render(main, monitor, 480);
                if (block >= 150) foreach (var s in main) sum += s * s;
            }
            Assert.True(core.NoiseSuppressionAvailable);
            return (float)Math.Sqrt(sum / (150 * 960));
        }

        var raw = BusRms(false);
        var cleaned = BusRms(true);
        output.WriteLine($"micro virtuel : {raw:0.0000} → {cleaned:0.0000}");
        Assert.True(cleaned < raw / 3);
    }

    private sealed class NoiseSource(float level) : NAudio.Wave.ISampleProvider
    {
        private readonly Random _rng = new(11);
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(Sr, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = level * (float)(_rng.NextDouble() * 2 - 1);
            return count;
        }
    }
}
