using SpokaSoundBoard.Core.Audio;

namespace SpokaSoundBoard.Tests;

public class DriftTests
{
    /// <summary>
    /// Producer and consumer clocks differ (as two sound cards always do). Before the adaptive resampling,
    /// this produced an underrun every ~30 seconds: the periodic drop-outs heard on Discord.
    /// </summary>
    [Theory]
    [InlineData(0.003)]
    [InlineData(0.001)]
    [InlineData(-0.001)]
    [InlineData(-0.003)]
    public void ClockDrift_IsAbsorbedWithoutDropouts(double drift)
    {
        const int rate = 48000;
        const int channels = 2;
        var ring = new FloatRingBuffer(rate * channels);
        var jb = new JitterBufferProvider(ring, rate, channels, 30f, 120f);

        var produceBlock = new float[2048 * channels];
        var output = new float[480 * channels];
        double phase = 0, owed = 0;
        var maxLevel = 0;
        var silentBlocksAfterStart = 0;

        // 2 minutes: the producer delivers 10 ms blocks at its own (drifting) rate, the consumer pulls 10 ms.
        for (var step = 0; step < 12000; step++)
        {
            owed += 480 * (1 + drift);
            var n = (int)owed;
            owed -= n;
            for (var i = 0; i < n; i++)
            {
                var s = (float)Math.Sin(phase);
                phase += 2 * Math.PI * 440 / rate;
                produceBlock[i * 2] = s;
                produceBlock[i * 2 + 1] = s;
            }
            ring.Write(produceBlock.AsSpan(0, n * channels));

            jb.Read(output, 0, output.Length);
            if (step > 100)
            {
                maxLevel = Math.Max(maxLevel, ring.Count / channels);
                if (output.Max(MathF.Abs) < 0.1f) silentBlocksAfterStart++;
            }
        }

        Assert.Equal(0, jb.Underruns);
        Assert.Equal(0, jb.Overruns);
        Assert.Equal(0, silentBlocksAfterStart);
        Assert.True(maxLevel < rate * 0.08, $"latence trop élevée : {maxLevel} trames");
        Assert.InRange(jb.Ratio, 1 + drift - 0.001, 1 + drift + 0.001);
    }

    [Fact]
    public void ResampledSignal_StaysClean()
    {
        const int rate = 48000;
        var ring = new FloatRingBuffer(rate);
        var jb = new JitterBufferProvider(ring, rate, 1, 20f, 80f);
        var block = new float[481];
        var output = new float[480];
        double phase = 0;
        var maxStep = 0f;
        var previous = 0f;
        for (var step = 0; step < 3000; step++)
        {
            for (var i = 0; i < block.Length; i++)
            {
                block[i] = (float)Math.Sin(phase);
                phase += 2 * Math.PI * 200 / rate;
            }
            ring.Write(block);
            jb.Read(output, 0, output.Length);
            foreach (var s in output)
            {
                if (step >= 50) maxStep = MathF.Max(maxStep, MathF.Abs(s - previous));
                previous = s;
            }
        }
        // A 200 Hz sine changes by at most ~0.026 per sample: any click would show up as a big jump.
        Assert.True(maxStep < 0.04f, $"discontinuité détectée : {maxStep}");
    }
}
