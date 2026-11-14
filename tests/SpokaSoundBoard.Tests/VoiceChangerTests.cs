using NAudio.Wave;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Speech;

namespace SpokaSoundBoard.Tests;

/// <summary>2.15: the real-time AI voice changer (SpokaVoiceEngine) and its link to the mixer.</summary>
public sealed class VoiceChangerTests
{
    private sealed class ArraySource(float[] data) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(MixerCore.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    private static float[] Sine(float freq, int samples, int rate, float amplitude = 0.5f) =>
        Enumerable.Range(0, samples).Select(i => amplitude * MathF.Sin(2 * MathF.PI * freq * i / rate)).ToArray();

    private static float Peak(IEnumerable<float> x) => x.Select(MathF.Abs).DefaultIfEmpty().Max();

    [Fact]
    public void Resampler_22kTo48k_KeepsTheTone_AndTheDuration()
    {
        var r = new StreamResampler(22050, 48000);
        var output = new List<float>();
        r.Process(Sine(1000, 22050, 22050), output);
        Assert.InRange(output.Count, 48000 - 40, 48000 + 1);
        Assert.InRange(Peak(output.Skip(200).Take(47000)), 0.48f, 0.52f);

        // A tone above the target's band would alias when going down: it must be filtered.
        var down = new StreamResampler(48000, 16000);
        var low = new List<float>();
        down.Process(Sine(12000, 48000, 48000), low);
        Assert.True(Peak(low.Skip(100)) < 0.01f, $"12 kHz leaked: {Peak(low.Skip(100))}");
    }

    [Fact]
    public void Resampler_GivesTheSameResult_WhateverTheBlockSizes()
    {
        var input = Sine(440, 20000, 22050).Zip(Sine(3100, 20000, 22050, 0.2f), (a, b) => a + b).ToArray();
        var whole = new List<float>();
        new StreamResampler(22050, 48000).Process(input, whole);
        var pieces = new List<float>();
        var r = new StreamResampler(22050, 48000);
        var rng = new Random(7);
        for (var i = 0; i < input.Length;)
        {
            var n = Math.Min(rng.Next(1, 3000), input.Length - i);
            r.Process(input.AsSpan(i, n), pieces);
            i += n;
        }
        Assert.Equal(whole.Count, pieces.Count);
        for (var i = 0; i < whole.Count; i++) Assert.Equal(whole[i], pieces[i], 5);
    }

    [Fact]
    public void Link_WaitsForABlock_ThenPlaysInOrder_AndStartsOverAfterAGap()
    {
        var link = new VoiceChangerLink { PrimeSamples = 1000 };
        var mic = Enumerable.Repeat(0.3f, 480).ToArray();
        var block = mic.ToArray();

        link.Exchange(block, micEnabled: true);
        Assert.All(block, s => Assert.Equal(0f, s));          // nothing converted yet: silence, not the real voice
        Assert.Equal(480, link.Input.Count);                  // but the microphone went to the engine

        link.Output.Write(Enumerable.Range(0, 1200).Select(i => i / 10000f).ToArray());
        block = mic.ToArray();
        link.Exchange(block, true);
        Assert.Equal(0f, block[0]);
        Assert.Equal(479 / 10000f, block[479], 6);

        block = mic.ToArray();
        link.Exchange(block, true);                           // 720 left: plays them
        block = mic.ToArray();
        link.Exchange(block, true);                           // 240 left: gap
        Assert.Equal(1, link.Underruns);
        Assert.Equal(1199 / 10000f, block[239], 6);
        Assert.Equal(0f, block[240]);

        // After a gap it waits for a whole block again.
        link.Output.Write(new float[500]);
        block = mic.ToArray();
        link.Exchange(block, true);
        Assert.Equal(500, link.Output.Count);
    }

    [Fact]
    public void Link_CatchesUp_WhenTooMuchIsBuffered_AndSendsSilenceWhenTheMicIsOff()
    {
        var link = new VoiceChangerLink { PrimeSamples = 1000, MaxExtraSamples = 500 };
        link.Output.Write(new float[5000]);
        var block = new float[480];
        link.Exchange(block, true);
        Assert.Equal(1000 - 480, link.Output.Count);          // kept one block of margin, not 5000 samples of delay

        link.Input.Clear();
        var mic = Enumerable.Repeat(0.5f, 480).ToArray();
        link.Exchange(mic, micEnabled: false);
        var sent = new float[480];
        link.Input.Read(sent);
        Assert.All(sent, s => Assert.Equal(0f, s));           // muted: the engine hears nothing
    }

    [Fact]
    public void Mixer_SendsTheMic_AndPlaysTheConvertedVoiceInstead()
    {
        var mix = new MixSettings();
        using var music = new MusicPlayer(MixerCore.SampleRate);
        using var pads = new SoundPadPlayer(MixerCore.SampleRate);
        var core = new MixerCore(new VoiceEffectSettings { GateEnabled = false, CompressorEnabled = false, VoiceThresholdDb = -60 }, mix, music, pads);
        core.SetMicSource(new ArraySource(Sine(250, 48000 * 2, 48000, 0.3f)));
        var link = new VoiceChangerLink { PrimeSamples = 4800 };
        core.VoiceChanger = link;
        var game = new float[4800 * 2];

        core.Render(game, Span<float>.Empty, Span<float>.Empty, 4800);
        Assert.True(Peak(game) < 0.001f, "the real voice reached the game");
        Assert.Equal(4800, link.Input.Count);

        // The « converted » voice: a different tone, louder.
        // (Within the catch-up limit: more than that at once would be dropped to keep the delay low.)
        link.Output.Write(Sine(600, 4800 * 3, 48000, 0.6f));
        for (var i = 0; i < 3; i++)
        {
            core.Render(game, Span<float>.Empty, Span<float>.Empty, 4800);
            if (i > 0) Assert.InRange(Peak(game), 0.4f, 0.8f);
        }

        core.VoiceChanger = null;
        for (var i = 0; i < 3; i++) core.Render(game, Span<float>.Empty, Span<float>.Empty, 4800);
        Assert.InRange(Peak(game), 0.2f, 0.4f);              // the real voice again
    }

    [Fact]
    public void Settings_KeepTheVoice_AndFixUnknownOnes()
    {
        var s = new AppSettings();
        s.VoiceChanger.VoiceKey = "F2";
        s.Hotkeys.ToggleVoiceChanger.Set(0x76, HotkeyModifiers.None);
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal("F2", back.VoiceChanger.VoiceKey);
        Assert.Equal(0x76, back.Hotkeys.ToggleVoiceChanger.VirtualKey);

        Assert.Equal(VoiceChangerCatalog.DefaultVoiceKey, SettingsStore.Deserialize("""{ "VoiceChanger": { "VoiceKey": "Z9" } }""").VoiceChanger.VoiceKey);
        Assert.Equal("m1", SettingsStore.Deserialize("""{ "VoiceChanger": { "VoiceKey": "m1" } }""").VoiceChanger.VoiceKey.ToLowerInvariant());
        var old = SettingsStore.Deserialize("{}");
        Assert.Equal(VoiceChangerCatalog.DefaultVoiceKey, old.VoiceChanger.VoiceKey);
        Assert.False(old.Hotkeys.ToggleVoiceChanger.IsSet);
    }

    [Fact]
    public void Catalog_IsPinned_AndHasEveryVoice()
    {
        var pack = VoiceChangerCatalog.SeedVc;
        Assert.Equal(NeuralModelKind.SeedVc, pack.Kind);
        Assert.All(pack.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        Assert.All(pack.Files, f => Assert.StartsWith("https://github.com/Spokator/SpokaSoundBoard/releases/download/voice-models-1/", pack.UrlOf(f).AbsoluteUri));
        foreach (var name in new[] { "content.onnx", "regulator.onnx", "dit.onnx", "f0.onnx", "hift.onnx", "source.json" })
            Assert.Contains(pack.Files, f => f.Name == name);
        Assert.Equal(10, VoiceChangerCatalog.Voices.Count);
        Assert.All(VoiceChangerCatalog.Voices, v => Assert.Contains(pack.Files, f => f.Name == v.FileName));
        Assert.All(VoiceChangerCatalog.Voices, v => Assert.False(string.IsNullOrEmpty(v.Description)));
        Assert.InRange(pack.TotalSize, 400_000_000, 440_000_000);
    }

    /// <summary>The real download from the GitHub release, through the app's model store (SPOKA_ONLINE_TESTS=1).</summary>
    [Fact]
    public async Task RealDownload_FromTheRelease_IsComplete()
    {
        if (Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "spoka_vc_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new VoiceModelStore(root);
            long last = 0;
            await store.InstallAsync(VoiceChangerCatalog.SeedVc, new Progress<VoiceDownloadProgress>(p => last = p.Done), CancellationToken.None);
            Assert.True(store.IsInstalled(VoiceChangerCatalog.SeedVc));
            Assert.All(VoiceChangerCatalog.SeedVc.Files, f => Assert.True(VoiceModelStore.IsVerified(store.FilePath(VoiceChangerCatalog.SeedVc, f.Name), f)));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The real engine on the real GPU, end to end through the session: opt-in, set SPOKA_VOICE_ENGINE (the exe) and
    /// SPOKA_VC_MODELS (a folder with the downloaded models) — needs a DirectX 12 graphics card.
    /// </summary>
    [Fact]
    public async Task RealEngine_ConvertsSpeechThroughTheSession()
    {
        var engine = Environment.GetEnvironmentVariable("SPOKA_VOICE_ENGINE");
        var models = Environment.GetEnvironmentVariable("SPOKA_VC_MODELS");
        var speech = Environment.GetEnvironmentVariable("SPOKA_VC_SPEECH");
        if (string.IsNullOrEmpty(engine) || string.IsNullOrEmpty(models) || string.IsNullOrEmpty(speech)) return;

        using var session = new VoiceChangerSession(engine, models);
        var ready = await session.StartAsync(Path.Combine(models, "voice_M5.bin"));
        Assert.False(ready.TooSlow);
        Assert.InRange(ready.BlockMs, 300, 600);

        // Real speech at 48 kHz, fed in real time (20 ms at a time) like the mixer does.
        float[] voice;
        using (var reader = new AudioFileReader(speech))
        {
            var mono = new List<float>();
            var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            int n;
            while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                for (var i = 0; i < n; i += reader.WaveFormat.Channels) mono.Add(buffer[i]);
            var r = new StreamResampler(reader.WaveFormat.SampleRate, 48000);
            var at48 = new List<float>();
            r.Process(mono.ToArray(), at48);
            voice = at48.Take(48000 * 6).ToArray();
        }
        var received = new List<float>();
        var chunk = new float[960];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var pos = 0; pos + 960 <= voice.Length + 48000; pos += 960)
        {
            session.Link.Input.Write(pos + 960 <= voice.Length ? voice.AsSpan(pos, 960) : new float[960]);
            while (sw.Elapsed.TotalMilliseconds < pos / 48.0) await Task.Delay(2);
            int got;
            while ((got = session.Link.Output.Read(chunk)) > 0) received.AddRange(chunk.Take(got));
        }
        await Task.Delay(1500);
        int last;
        while ((last = session.Link.Output.Read(chunk)) > 0) received.AddRange(chunk.Take(last));

        Assert.True(received.Count > 48000 * 5, $"only {received.Count} samples back");
        var rms = Math.Sqrt(received.Select(s => (double)s * s).Average());
        Assert.True(rms > 0.01, $"converted voice too quiet: {rms}");
        session.Stop();
    }
}
