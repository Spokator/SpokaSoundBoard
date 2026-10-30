using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Tests;

public class EffectsTests
{
    private const int Rate = 48000;

    private static float[] Sine(float freq, float amp, int samples) =>
        Enumerable.Range(0, samples).Select(i => amp * MathF.Sin(2 * MathF.PI * freq * i / Rate)).ToArray();

    private static float Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var s in x) sum += s * s;
        return (float)Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    [Theory]
    [InlineData(60f, 6f, true)]
    [InlineData(10000f, 6f, false)]
    public void Shelves_ApplyTheirGain(float freq, float gainDb, bool low)
    {
        var f = new Biquad();
        if (low) f.SetLowShelf(Rate, 200f, gainDb); else f.SetHighShelf(Rate, 4000f, gainDb);
        var input = Sine(freq, 0.25f, Rate / 2);
        var output = input.Select(f.Process).ToArray();
        var measured = DspUtil.LinearToDb(Rms(output.AsSpan(Rate / 4)) / Rms(input.AsSpan(Rate / 4)));
        Assert.InRange(measured, gainDb - 1f, gainDb + 1f);
    }

    [Fact]
    public void NasaPreset_PlaysQuindarToneAtStartAndEnd()
    {
        var settings = BuiltInPresets.All.First(p => p.Name == "Astronaute (NASA)").Settings.Clone();
        settings.VoiceThresholdDb = -40f;
        settings.RadioNoise = 0f;
        var vp = new VoiceProcessor(Rate, settings);

        // Start of transmission: the 2525 Hz tone must dominate the first 250 ms over a 200 Hz voice.
        var output = new float[Rate / 4];
        vp.Process(Sine(200, 0.3f, output.Length), output, true, false, false);
        Assert.True(ToneLevel(output, 2525) > 0.02f, $"start={ToneLevel(output, 2525)} transmitting={vp.Transmitting}");

        var tail = new float[Rate];
        vp.Process(new float[Rate], tail, true, false, false);
        Assert.True(ToneLevel(tail, 2475) > 0.01f, $"end={ToneLevel(tail, 2475)}");
    }

    [Fact]
    public void ChorusAndLofi_StayFiniteAndAudible()
    {
        var chorus = new ChorusEffect(Rate) { Depth = 1, Mix = 1, Rate = 5 };
        var lofi = new LofiEffect { Bits = 4, Downsample = 8, Drive = 1 };
        var input = Sine(300, 0.5f, Rate);
        var c = input.Select(chorus.Process).ToArray();
        var l = input.Select(lofi.Process).ToArray();
        Assert.All(c, s => Assert.True(float.IsFinite(s)));
        Assert.All(l, s => Assert.True(float.IsFinite(s) && MathF.Abs(s) <= 1f));
        Assert.True(Rms(c) > 0.1f);
        Assert.True(Rms(l) > 0.1f);
    }

    [Fact]
    public void Presets_HaveUniqueNames()
    {
        var names = BuiltInPresets.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.True(names.Count >= 20);
    }

    /// <summary>Goertzel magnitude of a single frequency.</summary>
    private static float ToneLevel(float[] x, float freq)
    {
        var w = 2 * Math.PI * freq / Rate;
        var coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (var v in x)
        {
            var s0 = v + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return (float)(Math.Sqrt(Math.Max(0, power)) / x.Length);
    }
}
