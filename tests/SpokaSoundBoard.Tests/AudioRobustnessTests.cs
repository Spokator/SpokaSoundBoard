using System.Diagnostics;
using NAudio.Wave;
using SpokaSoundBoard.Core.Audio;

namespace SpokaSoundBoard.Tests;

/// <summary>2.9: the audio thread never waits for the disk (decoders ahead, sounds in memory) and is measured.</summary>
public sealed class AudioRobustnessTests
{
    private const int Sr = 48000;

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>Seekable stereo float stream whose sample value is the frame index / 1e6 (easy to check continuity).</summary>
    private sealed class CountingStream(long frames, int delayMsPerRead = 0) : WaveStream
    {
        private long _position;

        public override WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Sr, 2);
        public override long Length => frames * 8;
        public bool Disposed { get; private set; }

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp((value + 4) / 8 * 8, 0, Length); // nearest frame, like real readers
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (delayMsPerRead > 0) Thread.Sleep(delayMsPerRead);
            var n = (int)Math.Min(count / 8 * 8, Length - _position);
            for (var i = 0; i < n; i += 8)
            {
                var value = (float)(_position / 8 + i / 8) / 1e6f;
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + i), value);
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + i + 4), value);
            }
            _position += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static DecodeAhead Async(CountingStream stream, double bufferSeconds = 0.5, long startFrame = 0, long pass = -1, bool loop = false)
    {
        if (startFrame > 0) stream.Position = startFrame * 8;
        var ahead = new DecodeAhead(stream, stream.ToSampleProvider(), Sr, bufferSeconds,
            TimeSpan.FromSeconds(startFrame / (double)Sr), pass, loop, inline: false);
        ahead.Prefill(0.1);
        return ahead;
    }

    /// <summary>Reads like the audio thread would, waiting for the decoder when it is behind (real time is slower).</summary>
    private static List<float> ReadAll(DecodeAhead ahead, int maxFrames)
    {
        var result = new List<float>();
        var block = new float[960];
        var watch = Stopwatch.StartNew();
        while (result.Count / 2 < maxFrames && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (ahead.IsWaiting || ahead.BufferedFrames == 0 && !ahead.IsEnded)
            {
                Thread.Sleep(1);
                continue;
            }
            var n = ahead.Read(block, 0, block.Length);
            if (n == 0) break;
            for (var i = 0; i < n; i++) result.Add(block[i]);
        }
        return result;
    }

    private static string WriteTone(double seconds, int rate = 44100)
    {
        var path = Path.Combine(Path.GetTempPath(), $"spoka29_{Guid.NewGuid():N}.wav");
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        var n = (int)(rate * seconds);
        for (var i = 0; i < n; i++)
        {
            var s = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
            writer.WriteSample(s);
            writer.WriteSample(s);
        }
        return path;
    }

    private static float Peak(ReadOnlySpan<float> x)
    {
        var p = 0f;
        foreach (var s in x) p = MathF.Max(p, MathF.Abs(s));
        return p;
    }

    // ---- RenderMonitor ----------------------------------------------------------------------------------------

    [Fact]
    public void Monitor_MeasuresLoadLateBlocksAndStalls()
    {
        var monitor = new RenderMonitor(Sr);
        var block = (long)(Stopwatch.Frequency * 0.010); // 480 frames = 10 ms
        long t = 1_000_000;

        // Start-up blocks: irregular by nature, never counted as stalls.
        monitor.Record(t, t + block / 10, 480);
        t += block * 10;
        for (var i = 1; i < RenderMonitor.WarmUpBlocks; i++, t += block) monitor.Record(t, t + block / 10, 480);
        Assert.Equal(0, monitor.Snapshot().Stalls);

        monitor.Record(t, t + block / 10, 480);              // 10 %
        t += block;
        monitor.Record(t, t + block * 6 / 10, 480);          // 60 %: slow
        t += block;
        monitor.Record(t, t + block * 2, 480);               // 200 %: late
        t += block * 10;                                     // the device waited 100 ms: stall
        monitor.Record(t, t + block / 10, 480);

        var h = monitor.Snapshot();
        Assert.Equal(RenderMonitor.WarmUpBlocks + 4, h.Blocks);
        Assert.Equal(1, h.SlowBlocks);
        Assert.Equal(1, h.LateBlocks);
        Assert.Equal(1, h.Stalls);
        Assert.InRange(h.PeakLoad, 1.99, 2.01);
        Assert.InRange(h.AverageLoad, 0.19, 0.21); // (20 × 0.1 + 0.1 + 0.6 + 2 + 0.1) / 24
        Assert.True(h.Glitches >= 2);

        monitor.Reset();
        var after = monitor.Snapshot();
        Assert.Equal(0, after.Blocks);
        Assert.Equal(0, after.Glitches);
    }

    [Fact]
    public void Monitor_RegularBlocks_AreHealthy()
    {
        var monitor = new RenderMonitor(Sr);
        var block = (long)(Stopwatch.Frequency * 0.010);
        long t = 5_000_000;
        for (var i = 0; i < 500; i++, t += block) monitor.Record(t, t + block / 20, 480);
        var h = monitor.Snapshot();
        Assert.Equal(0, h.LateBlocks + h.SlowBlocks + h.Stalls);
        Assert.InRange(h.AverageLoad, 0.049, 0.051);
    }

    // ---- DecodeAhead (real thread pool) -----------------------------------------------------------------------

    [Fact]
    public void DecodeAhead_DeliversEverySampleInOrder_ThenEnds()
    {
        var stream = new CountingStream(Sr * 2);
        using var ahead = Async(stream, bufferSeconds: 0.3);
        var samples = ReadAll(ahead, Sr * 3);
        Assert.Equal(Sr * 2 * 2, samples.Count);
        for (var f = 0; f < Sr * 2; f += 997) Assert.Equal(f / 1e6f, samples[f * 2], 6);
        Assert.True(ahead.IsEnded);
        Assert.Equal(0, ahead.Read(new float[64], 0, 64));
    }

    [Fact]
    public void DecodeAhead_Read_NeverBlocks_EvenOnAVerySlowDisk()
    {
        var before = RenderMonitor.DecodeUnderrunsTotal;
        var stream = new CountingStream(Sr * 10, delayMsPerRead: 200);
        using var ahead = new DecodeAhead(stream, stream.ToSampleProvider(), Sr, 0.5, default, -1, false, inline: false);
        var block = new float[960];
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            var started = watch.ElapsedTicks;
            var n = ahead.Read(block, 0, block.Length);
            Assert.True(n > 0, "silence, not the end of the stream");
            Assert.True(TimeSpan.FromTicks(watch.ElapsedTicks - started).TotalMilliseconds < 50, "the audio thread must not wait");
        }
        Assert.True(RenderMonitor.DecodeUnderrunsTotal > before, "the decoder being late is reported");
    }

    [Fact]
    public void DecodeAhead_Seek_WaitsThenContinuesFromTheTarget()
    {
        var stream = new CountingStream(Sr * 5);
        using var ahead = Async(stream);
        ReadAll(ahead, 4800);

        ahead.Seek(TimeSpan.FromSeconds(3));
        var block = new float[960];
        // Until the decoder has repositioned the file, Read outputs silence (never stale samples).
        if (ahead.IsWaiting) Assert.Equal(0f, Peak(block.AsSpan(0, ahead.Read(block, 0, block.Length))));

        var samples = ReadAll(ahead, 1000);
        Assert.Equal(3.0f * Sr / 1e6f, samples[0], 5);
        Assert.Equal((3 * Sr + 999) / 1e6f, samples[999 * 2], 5);
    }

    [Fact]
    public void DecodeAhead_RegionAndLoop_AreSeamless()
    {
        var stream = new CountingStream(Sr * 2);
        using var ahead = Async(stream, startFrame: 1000, pass: 3000, loop: true);
        var samples = ReadAll(ahead, 7000);
        for (var f = 0; f < 7000; f++)
            Assert.Equal((1000 + f % 3000) / 1e6f, samples[f * 2], 6);
    }

    [Fact]
    public void DecodeAhead_Dispose_ReleasesTheFile_EvenWhileDecoding()
    {
        var stream = new CountingStream(Sr * 30, delayMsPerRead: 30);
        var ahead = new DecodeAhead(stream, stream.ToSampleProvider(), Sr, 2, default, -1, false, inline: false);
        ahead.Read(new float[960], 0, 960); // starts a background fill
        ahead.Dispose();
        var watch = Stopwatch.StartNew();
        while (!stream.Disposed && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
        Assert.True(stream.Disposed);
        Assert.Equal(0, ahead.Read(new float[960], 0, 960));
    }

    [Fact]
    public void DecodeAhead_Open_RealFile_RespectsStartAndEnd()
    {
        var path = WriteTone(3);
        try
        {
            using var ahead = DecodeAhead.Open(path, Sr, 1, 0.2, startSeconds: 1, endSeconds: 1.5);
            Assert.InRange(ahead.Duration.TotalSeconds, 2.99, 3.01);
            var total = 0;
            var block = new float[4800];
            for (var i = 0; i < 200; i++)
            {
                if (ahead.IsWaiting || ahead.BufferedFrames == 0 && !ahead.IsEnded) { Thread.Sleep(2); continue; }
                var n = ahead.Read(block, 0, block.Length);
                if (n == 0) break;
                total += n;
            }
            Assert.InRange(total / 2, (int)(0.49 * Sr), (int)(0.51 * Sr));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- SoundCache -------------------------------------------------------------------------------------------

    [Fact]
    public void SoundCache_DecodesOnce_AtTheMixerRate()
    {
        var path = WriteTone(0.5);
        try
        {
            var cache = new SoundCache(Sr);
            Assert.Null(cache.TryGet(path));
            var sound = cache.GetOrLoad(path);
            Assert.NotNull(sound);
            Assert.InRange(sound!.Frames, (int)(0.49 * Sr), (int)(0.51 * Sr)); // resampled from 44.1 kHz
            Assert.Same(sound, cache.TryGet(path));
            Assert.Same(sound, cache.GetOrLoad(path));
            Assert.Equal(1, cache.Count);
            Assert.Equal(sound.Bytes, cache.Bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SoundCache_SkipsLongSounds_AndReloadsChangedFiles()
    {
        var longPath = WriteTone(3);
        var path = WriteTone(0.2);
        try
        {
            var cache = new SoundCache(Sr, maxSeconds: 2);
            Assert.Null(cache.GetOrLoad(longPath));
            Assert.Equal(0, cache.Count);

            var first = cache.GetOrLoad(path)!;
            File.Delete(path);
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Sr, 2)))
                writer.WriteSamples(new float[Sr * 2 / 2], 0, Sr); // 0.5 s of silence
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var second = cache.GetOrLoad(path)!;
            Assert.NotSame(first, second);
            Assert.Equal(Sr / 2, second.Frames);

            cache.Retain([]);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.Bytes);
        }
        finally
        {
            File.Delete(longPath);
            File.Delete(path);
        }
    }

    [Fact]
    public void SoundCache_DropsTheLeastRecentlyPlayed_OverBudget()
    {
        var paths = Enumerable.Range(0, 3).Select(_ => WriteTone(0.5, Sr)).ToList();
        try
        {
            var oneSound = Sr / 2 * 2 * sizeof(float);
            var cache = new SoundCache(Sr, maxBytes: oneSound * 2 + 100);
            cache.GetOrLoad(paths[0]);
            cache.GetOrLoad(paths[1]);
            cache.TryGet(paths[0]); // played again: the second one is now the oldest
            cache.GetOrLoad(paths[2]);
            Assert.NotNull(cache.TryGet(paths[0]));
            Assert.Null(cache.TryGet(paths[1]));
            Assert.NotNull(cache.TryGet(paths[2]));
        }
        finally
        {
            foreach (var p in paths) File.Delete(p);
        }
    }

    [Fact]
    public void CachedSoundSource_PlaysTheRegion_AndLoopsWithoutGap()
    {
        var samples = new float[1000 * 2];
        for (var f = 0; f < 1000; f++) samples[f * 2] = samples[f * 2 + 1] = f;
        var sound = new CachedSound("x", samples, DateTime.UtcNow, 0);

        var once = new CachedSoundSource(sound, 1000, startSeconds: 0.1, endSeconds: 0.3);
        var buffer = new float[1000];
        Assert.Equal(400, once.Read(buffer, 0, buffer.Length));
        Assert.Equal(100f, buffer[0]);
        Assert.Equal(299f, buffer[398]);
        Assert.Equal(0, once.Read(buffer, 0, buffer.Length));

        var looped = new CachedSoundSource(sound, 1000, startSeconds: 0.1, endSeconds: 0.3, loop: true);
        Assert.Equal(1000, looped.Read(buffer, 0, buffer.Length));
        for (var f = 0; f < 500; f++) Assert.Equal(100 + f % 200, buffer[f * 2]);
    }

    // ---- players ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pads_CachedSound_StartsOnTheVeryNextBlock()
    {
        var path = WriteTone(0.3);
        try
        {
            var cache = new SoundCache(Sr);
            await cache.PreloadAsync([path]);
            using var pads = new SoundPadPlayer(Sr, cache);
            var task = pads.PlayAsync(Guid.NewGuid(), path, 1f, false);
            Assert.True(task.IsCompleted, "no file to open: the sound is added right away");
            var block = new float[960];
            pads.Read(block, 0, block.Length);
            Assert.True(Peak(block) > 0.05f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Pads_LongSound_IsStreamed_AndOneOffFilesAreNotCached()
    {
        var path = WriteTone(1.5);
        try
        {
            var cache = new SoundCache(Sr, maxSeconds: 1);
            using var pads = new SoundPadPlayer(Sr, cache);
            var id = Guid.NewGuid();
            await pads.PlayAsync(id, path, 1f, false);
            Assert.Equal(0, cache.Count);
            var buffer = new float[Sr * 2 * 2];
            pads.Read(buffer, 0, buffer.Length);
            Assert.True(Peak(buffer) > 0.3f);
            Assert.False(pads.IsPlaying(id));

            var tts = WriteTone(0.2);
            try
            {
                await pads.PlayAsync(Guid.NewGuid(), tts, 1f, false, cache: false);
                Assert.Equal(0, cache.Count);
            }
            finally
            {
                pads.StopAll();
                pads.Read(buffer, 0, 9600);
                pads.Dispose();
                File.Delete(tts);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Music_AsyncSeek_StaysSilentUntilReady_ThenFadesIn()
    {
        var stream = new CountingStream(Sr * 20);
        var ahead = new DecodeAhead(stream, stream.ToSampleProvider(), Sr, 1, default, -1, false, inline: false);
        ahead.Prefill(0.2);
        using var player = new MusicPlayer(Sr);
        player.Install(new MusicDeck("compteur", ahead, Sr), 1f, true);

        var block = new float[960];
        player.Read(block, 0, block.Length);
        Assert.True(block[^2] > 0f);

        player.Seek(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), player.Position);

        // Sample value = frame / 1e6: around 10 s it is ≈ 0.48, before the seek it was ≈ 0.001.
        // Render until the new position is audible: it must rise from silence (micro fade), not jump in.
        var watch = Stopwatch.StartNew();
        var rising = false;
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            player.Read(block, 0, block.Length);
            var firstLoud = Array.FindIndex(block, s => s > 0.1f);
            if (firstLoud >= 0)
            {
                rising = firstLoud > 0 && block[firstLoud - 1] < block[^2];
                break;
            }
            Thread.Sleep(1);
        }
        Assert.True(rising, "fade-in after the seek");
        for (var i = 0; i < 50; i++) player.Read(block, 0, block.Length);
        var position = player.Position.TotalSeconds;
        Assert.InRange(position, 10.4, 11.5);
        Assert.InRange(block[^2], (float)(position * Sr / 1e6) - 0.002f, (float)(position * Sr / 1e6) + 0.002f);
    }
}
