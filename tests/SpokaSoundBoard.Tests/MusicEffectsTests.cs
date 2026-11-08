using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Tests;

public sealed class MusicEffectsTests
{
    private const int Sr = 48000;
    private const int Block = 480;

    private static float[] Stereo(int frames, Func<int, (float L, float R)> f)
    {
        var x = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var (l, r) = f(i);
            x[i * 2] = l;
            x[i * 2 + 1] = r;
        }
        return x;
    }

    private static float Sin(float freq, int i, float amp = 1f) => amp * MathF.Sin(2 * MathF.PI * freq * i / Sr);

    private static float[] Run(MusicEffectsChain chain, float[] input, MusicEffectSettings? settings)
    {
        var output = (float[])input.Clone();
        for (var i = 0; i < output.Length; i += Block * 2)
            chain.Process(output.AsSpan(i, Math.Min(Block * 2, output.Length - i)), settings);
        return output;
    }

    /// <summary>Goertzel power of one channel at <paramref name="freq"/> over [start, start + frames).</summary>
    private static double Power(float[] x, float freq, int start, int frames, int channel = 0)
    {
        var w = 2 * Math.PI * freq / Sr;
        var coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (var i = start; i < start + frames; i++)
        {
            var s = x[i * 2 + channel] + coeff * s1 - s2;
            s2 = s1;
            s1 = s;
        }
        return (s1 * s1 + s2 * s2 - coeff * s1 * s2) / frames / frames;
    }

    private static float MaxStep(float[] x, int channel = 0)
    {
        var max = 0f;
        for (var i = 2 + channel; i < x.Length; i += 2) max = MathF.Max(max, MathF.Abs(x[i] - x[i - 2]));
        return max;
    }

    [Fact]
    public void NeutralSettings_AreBypassed_BitIdentical()
    {
        var input = Stereo(Sr / 2, i => (Sin(440, i, 0.5f), Sin(330, i, 0.4f)));
        var chain = new MusicEffectsChain(Sr);
        Assert.Equal(input, Run(chain, input, new MusicEffectSettings()));
        Assert.Equal(input, Run(chain, input, null));
        Assert.Equal(input, Run(chain, input, new MusicEffectSettings { Enabled = false, BassDb = 12, Karaoke = true }));
        Assert.False(chain.IsActive);
        Assert.True(MusicEffectsChain.IsChainNeutral(new MusicEffectSettings { Speed = 1.5f }));
    }

    [Fact]
    public void BassBoost_IncreasesLowFrequencyEnergy()
    {
        var input = Stereo(Sr, i => (Sin(60, i, 0.2f) + Sin(5000, i, 0.2f), Sin(60, i, 0.2f) + Sin(5000, i, 0.2f)));
        var output = Run(new MusicEffectsChain(Sr), input, new MusicEffectSettings { BassDb = 12 });
        var start = Sr / 2;
        var lowBefore = Power(input, 60, start, Sr / 2);
        var lowAfter = Power(output, 60, start, Sr / 2);
        var highBefore = Power(input, 5000, start, Sr / 2);
        var highAfter = Power(output, 5000, start, Sr / 2);
        Assert.True(lowAfter > lowBefore * 2.5, $"low {lowBefore} -> {lowAfter}");
        Assert.True(lowAfter / highAfter > 10 * lowBefore / highBefore);
    }

    [Fact]
    public void Treble_AndEqBand_BoostTheirFrequencies()
    {
        var input = Stereo(Sr, i => (Sin(1000, i, 0.1f) + Sin(12000, i, 0.1f), Sin(1000, i, 0.1f) + Sin(12000, i, 0.1f)));
        var treble = Run(new MusicEffectsChain(Sr), input, new MusicEffectSettings { TrebleDb = 10 });
        Assert.True(Power(treble, 12000, Sr / 2, Sr / 2) / Power(treble, 1000, Sr / 2, Sr / 2) > 5);

        var eq = new MusicEffectSettings { EqEnabled = true, Eq1k = 12 };
        var boosted = Run(new MusicEffectsChain(Sr), input, eq);
        Assert.True(Power(boosted, 1000, Sr / 2, Sr / 2) / Power(boosted, 12000, Sr / 2, Sr / 2) > 5);

        // The same EQ values with the EQ switched off: neutral.
        eq.EqEnabled = false;
        Assert.Equal(input, Run(new MusicEffectsChain(Sr), input, eq));
    }

    [Fact]
    public void Karaoke_RemovesTheCentre_ButKeepsTheBass()
    {
        var input = Stereo(Sr, i =>
        {
            var s = Sin(1000, i, 0.3f) + Sin(60, i, 0.3f);
            return (s, s);
        });
        var output = Run(new MusicEffectsChain(Sr), input, new MusicEffectSettings { Karaoke = true });
        var start = Sr / 2;
        for (var ch = 0; ch < 2; ch++)
        {
            Assert.True(Power(output, 1000, start, Sr / 2, ch) < Power(input, 1000, start, Sr / 2, ch) * 0.01);
            Assert.True(Power(output, 60, start, Sr / 2, ch) > Power(input, 60, start, Sr / 2, ch) * 0.5);
        }
    }

    [Fact]
    public void Karaoke_KeepsSidePanned_Instruments()
    {
        var input = Stereo(Sr, i => (Sin(1000, i, 0.3f), 0f)); // hard-left guitar
        var output = Run(new MusicEffectsChain(Sr), input, new MusicEffectSettings { Karaoke = true });
        Assert.True(Power(output, 1000, Sr / 2, Sr / 2) > Power(input, 1000, Sr / 2, Sr / 2) * 0.2);
    }

    [Fact]
    public void Width_Zero_IsMono()
    {
        var input = Stereo(Sr / 2, i => (Sin(440, i, 0.5f), Sin(660, i, 0.3f)));
        var output = Run(new MusicEffectsChain(Sr), input, new MusicEffectSettings { StereoWidth = 0 });
        for (var f = Sr / 4; f < Sr / 2; f++) Assert.Equal(output[f * 2], output[f * 2 + 1], 5);
    }

    [Fact]
    public void Surround8D_MovesTheSoundFromSideToSide()
    {
        var input = Stereo(Sr * 3, i => (Sin(500, i, 0.4f), Sin(500, i, 0.4f)));
        var output = Run(new MusicEffectsChain(Sr), input, new MusicEffectSettings { Surround8D = true, Surround8DRate = 0.5f });
        var minRatio = double.MaxValue;
        var maxRatio = 0.0;
        for (var start = Sr / 4; start + Sr / 20 < Sr * 3; start += Sr / 20)
        {
            var l = Power(output, 500, start, Sr / 20, 0);
            var r = Power(output, 500, start, Sr / 20, 1);
            var ratio = l / (r + 1e-12);
            minRatio = Math.Min(minRatio, ratio);
            maxRatio = Math.Max(maxRatio, ratio);
        }
        Assert.True(maxRatio > 4, $"max L/R {maxRatio}");
        Assert.True(minRatio < 0.25, $"min L/R {minRatio}");
        Assert.True(MaxStep(output) < 0.1f);
    }

    [Fact]
    public void Reverb_AddsADecayingTail()
    {
        var input = new float[Sr * 3 * 2];
        for (var i = 0; i < 480; i++) input[i * 2] = input[i * 2 + 1] = Sin(800, i, 0.8f);
        var output = Run(new MusicEffectsChain(Sr), input, new MusicEffectSettings { ReverbMix = 0.6f });
        float Energy(int from, int to)
        {
            var e = 0f;
            for (var i = from * 2; i < to * 2; i++) e += output[i] * output[i];
            return e;
        }
        var early = Energy(Sr / 10, Sr / 2);
        var late = Energy(Sr * 2, Sr * 3);
        Assert.True(early > 1e-3f, $"early {early}");
        Assert.True(late < early);
        Assert.All(output, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void ExtremeSettings_NeverProduceNaN_OrExceedFullScale()
    {
        var noise = new Random(7);
        var input = Stereo(Sr * 2, i => (Sin(50, i, 0.9f) + (float)(noise.NextDouble() - 0.5) * 0.4f, Sin(80, i, 0.9f)));
        var settings = new MusicEffectSettings
        {
            BassDb = 15, TrebleDb = 10, EqEnabled = true, Surround8D = true, Surround8DRate = 2f, Karaoke = true,
            ReverbMix = 1f, StereoWidth = 2f,
        };
        settings.SetEqBands(Enumerable.Repeat(12f, 10).ToArray());
        var output = Run(new MusicEffectsChain(Sr), input, settings);
        Assert.All(output, s => Assert.True(float.IsFinite(s) && MathF.Abs(s) <= 1f, $"sample {s}"));

        // Garbage values are sanitized.
        var garbage = new MusicEffectSettings
        {
            BassDb = float.NaN, TrebleDb = float.PositiveInfinity, StereoWidth = float.NaN, ReverbMix = 50, Surround8D = true,
            Surround8DRate = float.NaN,
        };
        output = Run(new MusicEffectsChain(Sr), input, garbage);
        Assert.All(output, s => Assert.True(float.IsFinite(s) && MathF.Abs(s) <= 1f, $"sample {s}"));
    }

    [Fact]
    public void SoftLimiter_IsTransparentBelowTheKnee()
    {
        Assert.Equal(0.5f, MusicEffectsChain.SoftClip(0.5f));
        Assert.Equal(-0.8f, MusicEffectsChain.SoftClip(-0.8f));
        Assert.InRange(MusicEffectsChain.SoftClip(3f), 0.95f, 1f);
        Assert.InRange(MusicEffectsChain.SoftClip(-30f), -1f, -0.95f);
    }

    [Fact]
    public void EnablingAndMovingSliders_NeverClicks()
    {
        var input = Stereo(Sr * 2, i => (Sin(200, i, 0.4f), Sin(200, i, 0.4f)));
        var chain = new MusicEffectsChain(Sr);
        var settings = new MusicEffectSettings();
        var output = (float[])input.Clone();
        var blockIndex = 0;
        for (var i = 0; i < output.Length; i += Block * 2, blockIndex++)
        {
            switch (blockIndex)
            {
                case 10: settings.BassDb = 12; break;           // enable from bypass
                case 30: settings.Karaoke = true; break;
                case 50: settings.StereoWidth = 0f; break;
                case 70: settings.ReverbMix = 0.5f; break;
                case 90: settings.EqEnabled = true; settings.Eq250 = -12; break;
                case 110: settings.Surround8D = true; break;
                case 130: settings.Enabled = false; break;        // back to bypass
                case 160: settings.Enabled = true; break;
            }
            chain.Process(output.AsSpan(i, Block * 2), settings);
        }
        // A 200 Hz sine at 0.4 moves by at most ~0.01 per sample; a click would be far above that.
        Assert.True(MaxStep(output) < 0.06f, $"max step {MaxStep(output)}");
        Assert.True(MaxStep(output, 1) < 0.06f, $"max step {MaxStep(output, 1)}");
    }

    [Fact]
    public void Spectrum_GetBands_IsSilentForSilence_AndFindsATone()
    {
        var analyzer = new SpectrumAnalyzer(Sr);
        var bands = new float[24];
        analyzer.Write(new float[4096 * 2]);
        analyzer.GetBands(bands);
        Assert.All(bands, b => Assert.Equal(0f, b));

        analyzer.Write(Stereo(4096, i => (Sin(100, i, 0.5f), Sin(100, i, 0.5f))));
        for (var i = 0; i < 5; i++) analyzer.GetBands(bands);
        var loudest = Array.IndexOf(bands, bands.Max());
        Assert.True(loudest < 6, $"loudest band {loudest}");
        Assert.True(bands[^1] < 0.2f);
        Assert.All(bands, b => Assert.InRange(b, 0f, 1f));
    }
}
