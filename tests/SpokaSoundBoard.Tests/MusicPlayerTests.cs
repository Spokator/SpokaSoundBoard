using NAudio.Wave;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Tests;

public sealed class MusicPlayerTests : IDisposable
{
    private const int Sr = 48000;
    private const int Block = 480;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spoka_music_" + Guid.NewGuid().ToString("N"));

    public MusicPlayerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best effort.
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    internal static float[] Tone(int frames, float freq, float amp = 0.5f, float phase = 0f, int sampleRate = Sr)
    {
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var s = amp * MathF.Sin(2 * MathF.PI * freq * i / sampleRate + phase);
            data[i * 2] = s;
            data[i * 2 + 1] = s;
        }
        return data;
    }

    private string Write(float[] interleaved, int sampleRate = Sr)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        writer.WriteSamples(interleaved, 0, interleaved.Length);
        return path;
    }

    private string WriteTone(double seconds, float freq, float amp = 0.5f, float phase = 0f) =>
        Write(Tone((int)(seconds * Sr), freq, amp, phase));

    private static float[] Render(MusicPlayer player, int frames, int block = Block)
    {
        var output = new float[frames * 2];
        var buffer = new float[block * 2];
        for (var done = 0; done < frames; done += block)
        {
            var n = Math.Min(block, frames - done);
            player.Read(buffer, 0, n * 2);
            Array.Copy(buffer, 0, output, done * 2, n * 2);
        }
        return output;
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
            Thread.Sleep(5);
        }
    }

    private static float MaxStep(float[] interleaved, int channel = 0)
    {
        var max = 0f;
        for (var i = 2 + channel; i < interleaved.Length; i += 2)
            max = MathF.Max(max, MathF.Abs(interleaved[i] - interleaved[i - 2]));
        return max;
    }

    private static float Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var s in x) sum += s * s;
        return (float)Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    private static float Peak(ReadOnlySpan<float> x)
    {
        var p = 0f;
        foreach (var s in x) p = MathF.Max(p, MathF.Abs(s));
        return p;
    }

    private static int ZeroCrossings(float[] interleaved, int startFrame, int frames)
    {
        var n = 0;
        for (var f = startFrame + 1; f < startFrame + frames; f++)
            if (interleaved[(f - 1) * 2] < 0 != interleaved[f * 2] < 0) n++;
        return n;
    }

    // ---- transitions ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(4.0)] // tracks too short for a crossfade: falls back to gapless
    public void Gapless_NextTrackStartsOnTheExactNextSample(double crossfadeSeconds)
    {
        var a = Tone(24_061, 440, 0.4f, 1.0f);   // ends mid-block, on a non-zero sample
        var b = Tone(33_337, 700, 0.3f, 0.5f);
        var pathA = Write(a);
        var pathB = Write(b);
        using var player = new MusicPlayer(Sr) { CrossfadeDuration = TimeSpan.FromSeconds(crossfadeSeconds) };
        var advanced = new List<string>();
        var ended = 0;
        player.TrackAdvanced += p => advanced.Add(p);
        player.TrackEnded += () => ended++;

        player.Load(pathA);
        player.SetNext(pathB, 1f);
        WaitUntil(() => player.IsNextReady);
        Assert.Equal(pathB, player.NextFile);

        var output = Render(player, 24_061 + 33_337 + 4800);

        for (var i = 0; i < a.Length; i++) Assert.Equal(a[i], output[i]);
        var firstBad = Enumerable.Range(0, b.Length).FirstOrDefault(i => b[i] != output[a.Length + i], -1);
        Assert.True(firstBad < 0, $"first mismatch at B sample {firstBad}: {b[Math.Max(0, firstBad)]} vs {output[a.Length + Math.Max(0, firstBad)]}, next {output[a.Length + Math.Max(0, firstBad) + 2]} prev {b[Math.Max(0, firstBad - 2)]}");
        Assert.Equal(0f, Peak(output.AsSpan(a.Length + b.Length)));
        Assert.Equal([pathB], advanced);
        Assert.Equal(1, ended);
        Assert.Equal(pathB, player.CurrentFile);
        Assert.Null(player.NextFile);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public void Crossfade_MixesBothTracksAtConstantPower_AndAdvancesOnce()
    {
        var pathA = WriteTone(21, 440);
        var pathB = WriteTone(21, 660);
        using var player = new MusicPlayer(Sr) { CrossfadeDuration = TimeSpan.FromSeconds(2) };
        var advanced = new List<string>();
        var ended = 0;
        player.TrackAdvanced += p => advanced.Add(p);
        player.TrackEnded += () => ended++;

        player.Load(pathA);
        Render(player, Sr / 10);
        player.SetNext(pathB, 1f);
        WaitUntil(() => player.IsNextReady);
        player.Seek(TimeSpan.FromSeconds(17));
        Render(player, Sr / 10);
        Assert.InRange(player.Position.TotalSeconds, 17.05, 17.15);
        Assert.False(player.IsCrossfading);

        // 17.1 s → 19 s: still track A alone.
        var before = Render(player, (int)(1.85 * Sr));
        Assert.Empty(advanced);
        Assert.False(player.IsCrossfading);

        // Crossfade from 19 s to 21 s.
        var steady = Rms(before.AsSpan(before.Length - 2 * Sr / 10));
        var sawCrossfade = false;
        var window = Sr / 50; // 20 ms
        var all = new List<float>();
        for (var t = 0; t < 3 * 50; t++)
        {
            var chunk = Render(player, window);
            all.AddRange(chunk);
            sawCrossfade |= player.IsCrossfading;
            var rms = Rms(chunk);
            Assert.InRange(rms, steady * 0.7f, steady * 1.3f);
        }

        Assert.True(sawCrossfade);
        Assert.Equal([pathB], advanced);
        Assert.Equal(0, ended);
        Assert.Equal(pathB, player.CurrentFile);
        Assert.False(player.IsCrossfading);
        Assert.InRange(player.Position.TotalSeconds, 2.85, 3.2);
        Assert.True(MaxStep(all.ToArray()) < 0.08f);
    }

    [Fact]
    public void TrackEnded_FiresAtTheRealEnd_WhenNothingIsQueued()
    {
        var path = WriteTone(0.3, 440);
        using var player = new MusicPlayer(Sr) { CrossfadeDuration = TimeSpan.FromSeconds(4) };
        var ended = 0;
        var advanced = 0;
        player.TrackEnded += () => ended++;
        player.TrackAdvanced += _ => advanced++;
        player.Load(path);

        var output = Render(player, Sr / 2);
        Assert.Equal(1, ended);
        Assert.Equal(0, advanced);
        Assert.False(player.IsPlaying);
        Assert.True(player.HasTrack);
        Assert.Equal(path, player.CurrentFile);
        Assert.True(Peak(output.AsSpan((int)(0.29 * Sr) * 2, 200)) > 0.3f); // played to the very end
        Assert.Equal(0f, Peak(output.AsSpan((int)(0.3 * Sr) * 2)));
    }

    [Fact]
    public void Load_WhilePlaying_CrossfadesWithoutDiscontinuity()
    {
        var pathA = WriteTone(2, 440);
        var pathB = Write(Tone(2 * Sr, 660, 0.5f, MathF.PI / 2)); // starts at +0.5: a hard cut would jump
        using var player = new MusicPlayer(Sr);
        player.Load(pathA);
        var first = Render(player, (int)(0.3 * Sr));
        player.Load(pathB);
        Assert.Equal(pathB, player.CurrentFile);
        Assert.True(player.IsCrossfading);
        var second = Render(player, (int)(0.5 * Sr));

        var joined = first.Concat(second).ToArray();
        Assert.True(MaxStep(joined) < 0.08f, $"max step {MaxStep(joined)}");
        Assert.False(player.IsCrossfading);
        Assert.InRange(Peak(second.AsSpan(second.Length - 9600)), 0.45f, 0.55f);
        Assert.InRange(player.Position.TotalSeconds, 0.49, 0.51);
    }

    [Fact]
    public void Load_WhenIdle_StartsOnTheFirstSample()
    {
        var data = Tone(Sr / 2, 440, 0.5f, MathF.PI / 2);
        var path = Write(data);
        using var player = new MusicPlayer(Sr);
        player.Load(path);
        var output = Render(player, 1000);
        Assert.Equal(data[0], output[0]);
        Assert.Equal(data[1999], output[1999]);
    }

    [Fact]
    public void Seek_MovesInFileTime_WithMicroFades()
    {
        var path = WriteTone(3, 440);
        using var player = new MusicPlayer(Sr);
        player.Load(path);
        var a = Render(player, Sr / 10);

        player.Seek(TimeSpan.FromSeconds(2.00123));
        Assert.Equal(TimeSpan.FromSeconds(2.00123), player.Position);
        var b = Render(player, Sr / 2);
        Assert.InRange(player.Position.TotalSeconds, 2.47, 2.51);
        Assert.True(MaxStep(a.Concat(b).ToArray()) < 0.08f);
        Assert.InRange(Peak(b.AsSpan(b.Length / 2)), 0.45f, 0.55f);

        player.Seek(TimeSpan.FromSeconds(10)); // clamped
        Assert.Equal(player.Duration, player.Position);

        player.Pause();
        Render(player, Sr / 10);
        player.Seek(TimeSpan.FromSeconds(1)); // silent: immediate
        Assert.Equal(1.0, player.Position.TotalSeconds, 3);
        player.Play();
        Render(player, Sr / 10);
        Assert.InRange(player.Position.TotalSeconds, 1.09, 1.11);
        Assert.InRange(player.Remaining.TotalSeconds, 1.89, 1.91);
    }

    [Fact]
    public void PauseAndPlay_UseMicroFades_AndFreezeThePosition()
    {
        var path = WriteTone(3, 440);
        using var player = new MusicPlayer(Sr);
        player.Load(path);
        var a = Render(player, Sr / 10);
        player.Pause();
        Assert.False(player.IsPlaying);
        var b = Render(player, Sr / 10);
        Assert.Equal(0f, Peak(b.AsSpan(b.Length / 2)));
        var frozen = player.Position;
        Render(player, Sr / 10);
        Assert.Equal(frozen, player.Position);
        Assert.InRange(frozen.TotalSeconds, 0.1, 0.13);

        player.Play();
        var c = Render(player, Sr / 10);
        Assert.True(MaxStep(a.Concat(b).Concat(c).ToArray()) < 0.08f);
        Assert.InRange(Peak(c.AsSpan(c.Length / 2)), 0.45f, 0.55f);
    }

    [Fact]
    public void Stop_FadesQuickly_AndReleasesTheFile()
    {
        var path = WriteTone(3, 440);
        using var player = new MusicPlayer(Sr);
        player.Load(path);
        var a = Render(player, Sr / 10);
        player.Stop();
        Assert.False(player.HasTrack);
        Assert.Null(player.CurrentFile);
        Assert.False(player.IsPlaying);
        var b = Render(player, Sr / 10);
        Assert.True(MaxStep(a.Concat(b).ToArray()) < 0.08f);
        Assert.Equal(0f, Peak(b.AsSpan(Sr / 20 * 2)));
        Render(player, Block);
        WaitUntil(() =>
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        });
    }

    // ---- next track -------------------------------------------------------------------------------------------

    [Fact]
    public void SetNext_WithUnreadableFile_RaisesNextTrackFailed_AndEndsNormally()
    {
        var path = WriteTone(0.3, 440);
        var corrupt = Path.Combine(_dir, "corrupt.wav");
        File.WriteAllBytes(corrupt, Enumerable.Range(0, 5000).Select(i => (byte)(i * 37)).ToArray());
        using var player = new MusicPlayer(Sr);
        var failures = new List<string>();
        using var failed = new ManualResetEventSlim();
        player.NextTrackFailed += (p, ex) =>
        {
            Assert.NotNull(ex);
            lock (failures) failures.Add(p);
            failed.Set();
        };
        var ended = 0;
        var advanced = 0;
        player.TrackEnded += () => ended++;
        player.TrackAdvanced += _ => advanced++;

        player.Load(path);
        player.SetNext(corrupt, 1f);
        Assert.True(failed.Wait(5000));
        Assert.Equal([corrupt], failures);
        Assert.Null(player.NextFile);
        Assert.False(player.IsNextReady);

        Render(player, Sr / 2);
        Assert.Equal(1, ended);
        Assert.Equal(0, advanced);

        failed.Reset();
        player.SetNext(Path.Combine(_dir, "missing.mp3"), 1f);
        Assert.True(failed.Wait(5000));
    }

    [Fact]
    public void SetNext_Null_CancelsTheQueuedTrack()
    {
        var pathA = WriteTone(0.3, 440);
        var pathB = WriteTone(0.3, 660);
        using var player = new MusicPlayer(Sr);
        var ended = 0;
        var advanced = 0;
        player.TrackEnded += () => ended++;
        player.TrackAdvanced += _ => advanced++;
        player.Load(pathA);
        player.SetNext(pathB, 1f);
        WaitUntil(() => player.IsNextReady);
        player.SetNext(null);
        Assert.Null(player.NextFile);
        Render(player, Sr / 2);
        Assert.Equal(1, ended);
        Assert.Equal(0, advanced);
        Assert.Equal(pathA, player.CurrentFile);
    }

    [Fact]
    public void Load_ReusesThePreloadedNextTrack()
    {
        var pathA = WriteTone(1, 440);
        var pathB = WriteTone(1, 660);
        using var player = new MusicPlayer(Sr);
        player.Load(pathA);
        player.SetNext(pathB, 0.5f);
        WaitUntil(() => player.IsNextReady);
        player.SetNext(pathB, 0.25f); // same file: only the gain changes
        Assert.True(player.IsNextReady);
        player.Load(pathB, 0.25f);
        Assert.Equal(pathB, player.CurrentFile);
        Assert.Null(player.NextFile);
        Assert.False(player.IsNextReady);
        Assert.Equal(0.25f, player.Gain);
        Assert.True(player.IsActive);
    }

    [Fact]
    public void Next_GainIsApplied_AfterTheTransition()
    {
        var pathA = WriteTone(0.2, 440);
        var pathB = WriteTone(0.5, 440);
        using var player = new MusicPlayer(Sr);
        player.Load(pathA, 1f);
        player.SetNext(pathB, 0.5f);
        WaitUntil(() => player.IsNextReady);
        var output = Render(player, (int)(0.6 * Sr));
        Assert.InRange(Peak(output.AsSpan(0, 9600)), 0.49f, 0.51f);
        Assert.InRange(Peak(output.AsSpan((int)(0.4 * Sr) * 2, 9600)), 0.24f, 0.26f);
        Assert.Equal(0.5f, player.Gain);
    }

    [Fact]
    public void Dispose_ReleasesBothDecks()
    {
        var pathA = WriteTone(1, 440);
        var pathB = WriteTone(1, 660);
        var player = new MusicPlayer(Sr);
        player.Load(pathA);
        player.SetNext(pathB, 1f);
        WaitUntil(() => player.IsNextReady);
        Render(player, Sr / 10);
        player.Dispose();
        File.Delete(pathA);
        File.Delete(pathB);
        Assert.False(File.Exists(pathA));
        var buffer = new float[960];
        Assert.Equal(960, player.Read(buffer, 0, 960));
    }

    [Fact]
    public void Dispose_AfterTransitions_ReleasesRetiredDecks()
    {
        var pathA = WriteTone(0.2, 440);
        var pathB = WriteTone(0.2, 660);
        var pathC = WriteTone(1, 550);
        var player = new MusicPlayer(Sr);
        player.Load(pathA);
        player.SetNext(pathB, 1f);
        WaitUntil(() => player.IsNextReady);
        Render(player, Sr / 2);
        player.Load(pathC);
        player.Load(pathA);
        Render(player, Sr / 10);
        player.Dispose();
        File.Delete(pathA);
        File.Delete(pathB);
        File.Delete(pathC);
    }

    // ---- robustness -------------------------------------------------------------------------------------------

    private sealed class ThrowingProvider(int framesBeforeFailure) : ISampleProvider
    {
        private int _left = framesBeforeFailure * 2;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Sr, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            if (_left <= 0) throw new InvalidDataException("corrupted frame");
            var n = Math.Min(count, _left);
            Array.Fill(buffer, 0.25f, offset, n);
            _left -= n;
            return n;
        }
    }

    [Fact]
    public void CorruptedStream_IsTreatedAsEndOfTrack_OnTheAudioThread()
    {
        using var player = new MusicPlayer(Sr);
        var ended = 0;
        player.TrackEnded += () => ended++;
        player.Install(new MusicDeck("corrupt.mp3", null, new ThrowingProvider(1000), TimeSpan.FromSeconds(10), Sr), 1f, true);
        var output = Render(player, 4800);
        Assert.Equal(1, ended);
        Assert.Equal(0.25f, output[0]);
        Assert.Equal(0f, output[1000 * 2]);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotBreakTheAudioThread()
    {
        var path = WriteTone(0.1, 440);
        using var player = new MusicPlayer(Sr);
        player.TrackEnded += () => throw new InvalidOperationException("UI bug");
        player.Load(path);
        Render(player, Sr / 5);
        Assert.False(player.IsPlaying);
    }

    // ---- varispeed --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2.0f, 2.0, 1.0)]
    [InlineData(0.5f, 1.0, 2.0)]
    [InlineData(1.25f, 2.0, 1.6)]
    public void Varispeed_ChangesWallClockDuration_ButPositionStaysInFileTime(float speed, double fileSeconds, double wallSeconds)
    {
        var path = WriteTone(fileSeconds, 440);
        using var player = new MusicPlayer(Sr) { Effects = new MusicEffectSettings { Speed = speed } };
        var ended = false;
        player.TrackEnded += () => ended = true;
        player.Load(path);
        Assert.Equal(fileSeconds, player.Duration.TotalSeconds, 3);

        var half = Render(player, (int)(wallSeconds / 2 * Sr));
        Assert.InRange(player.Position.TotalSeconds, fileSeconds / 2 - 0.02, fileSeconds / 2 + 0.02);
        Assert.InRange(player.RemainingRealTime.TotalSeconds, wallSeconds / 2 - 0.02, wallSeconds / 2 + 0.02);

        // Pitch follows the speed.
        var crossings = ZeroCrossings(half, Sr / 10, Sr / 5);
        Assert.InRange(crossings / 2.0 / 0.2, 440 * speed * 0.98, 440 * speed * 1.02);

        var frames = (int)(wallSeconds / 2 * Sr);
        while (!ended && frames < wallSeconds * 2 * Sr)
        {
            Render(player, Block);
            frames += Block;
        }
        Assert.True(ended);
        Assert.InRange(frames / (double)Sr, wallSeconds - 0.02, wallSeconds + 0.02);
    }

    [Fact]
    public void Varispeed_CrossfadeStartsEarlierInFileTime_WhenFaster()
    {
        var pathA = WriteTone(30, 440);
        var pathB = WriteTone(30, 660);
        var effects = new MusicEffectSettings { Speed = 2f };
        using var player = new MusicPlayer(Sr) { CrossfadeDuration = TimeSpan.FromSeconds(2), Effects = effects };
        var advanced = 0;
        player.TrackAdvanced += _ => advanced++;
        player.Load(pathA);
        player.SetNext(pathB, 1f);
        WaitUntil(() => player.IsNextReady);
        player.Seek(TimeSpan.FromSeconds(24));
        Render(player, Block);

        // 2 s of real time left = 4 s of file time: the crossfade starts at 26 s (file time).
        Render(player, (int)(0.95 * Sr));   // ~25.9 s
        Assert.Equal(0, advanced);
        Render(player, (int)(0.1 * Sr));    // ~26.1 s
        Assert.Equal(1, advanced);
        Assert.True(player.IsCrossfading);
    }

    [Fact]
    public void SpeedChange_WhilePlaying_DoesNotClick()
    {
        var path = WriteTone(3, 440);
        var effects = new MusicEffectSettings();
        using var player = new MusicPlayer(Sr) { Effects = effects };
        player.Load(path);
        var a = Render(player, Sr / 10);
        effects.Speed = 1.25f;
        var b = Render(player, Sr / 5);
        effects.Speed = 0.8f;
        var c = Render(player, Sr / 5);
        effects.Speed = 1f;
        var d = Render(player, Sr / 5);
        Assert.True(MaxStep(a.Concat(b).Concat(c).Concat(d).ToArray()) < 0.08f);
    }

    [Fact]
    public void Varispeed_ResampledSource_KeepsFileTime()
    {
        // 44.1 kHz file: resampled by the loader, then varispeed.
        var path = Path.Combine(_dir, "cd.wav");
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)))
        {
            var data = Tone(44100 * 2, 440, 0.5f, 0, 44100);
            writer.WriteSamples(data, 0, data.Length);
        }
        using var player = new MusicPlayer(Sr) { Effects = new MusicEffectSettings { Speed = 1.5f } };
        player.Load(path);
        Render(player, Sr / 2);
        Assert.InRange(player.Position.TotalSeconds, 0.73, 0.77);
    }

    // ---- effects through the player ---------------------------------------------------------------------------

    [Fact]
    public void NeutralEffects_AreBitIdenticalToNoEffects()
    {
        var data = Tone(Sr, 440, 0.5f, 0.3f);
        for (var i = 0; i < data.Length; i += 2) data[i] *= 0.7f; // not mono
        var path = Write(data);

        float[] Run(MusicEffectSettings? effects)
        {
            using var player = new MusicPlayer(Sr) { Effects = effects };
            player.Load(path);
            return Render(player, Sr / 2);
        }

        var none = Run(null);
        var neutral = Run(new MusicEffectSettings());
        var disabled = Run(new MusicEffectSettings
        {
            Enabled = false, BassDb = 15, Speed = 2, Karaoke = true, ReverbMix = 1, Surround8D = true, StereoWidth = 0,
        });

        Assert.Equal(data.AsSpan(0, none.Length).ToArray(), none);
        Assert.Equal(none, neutral);
        Assert.Equal(none, disabled);
    }

    [Fact]
    public void ExtremeEffects_NeverOutputNaNOrClip()
    {
        var data = Tone(Sr * 2, 60, 0.6f);
        var noise = new Random(1);
        for (var i = 0; i < data.Length; i++) data[i] = Math.Clamp(data[i] + (float)(noise.NextDouble() - 0.5) * 0.7f, -1f, 1f);
        var path = Write(data);
        var effects = new MusicEffectSettings
        {
            BassDb = 15, TrebleDb = 10, EqEnabled = true, Speed = 2f, Surround8D = true, Surround8DRate = 0.5f,
            Karaoke = true, ReverbMix = 1f, StereoWidth = 2f,
        };
        effects.SetEqBands(Enumerable.Repeat(12f, 10).ToArray());
        using var player = new MusicPlayer(Sr) { Effects = effects };
        player.Load(path, 3f);
        var output = Render(player, Sr);
        Assert.All(output, s => Assert.True(float.IsFinite(s) && MathF.Abs(s) <= 1f, $"sample {s}"));
        Assert.True(Peak(output) > 0.5f);
    }

    [Fact]
    public void Spectrum_ShowsTheDominantFrequency()
    {
        var path = WriteTone(1, 1000, 0.8f);
        using var player = new MusicPlayer(Sr);
        player.Load(path);
        Render(player, Sr / 4);
        var bands = new float[32];
        for (var i = 0; i < 5; i++) player.Spectrum.GetBands(bands);
        var loudest = Array.IndexOf(bands, bands.Max());
        var min = player.Spectrum.MinFrequency;
        var ratio = player.Spectrum.MaxFrequency / min;
        var lo = min * MathF.Pow(ratio, loudest / 32f);
        var hi = min * MathF.Pow(ratio, (loudest + 1) / 32f);
        Assert.InRange(1000f, lo * 0.85f, hi * 1.15f);
        Assert.True(bands[loudest] > 0.8f);
        Assert.True(bands[0] < 0.3f);
    }
}
