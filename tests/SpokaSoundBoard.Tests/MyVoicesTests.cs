using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Speech;

namespace SpokaSoundBoard.Tests;

/// <summary>2.16: « Mes voix » (voices made from recordings) and the reactive mode of the voice changer.</summary>
public sealed class MyVoicesTests
{
    private const int Rate = 48000;

    /// <summary>A « voice »: a harmonic tone with a syllable-like envelope.</summary>
    private static float[] Voice(double seconds, float amplitude = 0.3f, double freq = 140)
    {
        var n = (int)(seconds * Rate);
        var x = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            var env = 0.6 + 0.4 * Math.Sin(2 * Math.PI * 4 * t);
            x[i] = (float)(amplitude * env * (Math.Sin(2 * Math.PI * freq * t) + 0.5 * Math.Sin(4 * Math.PI * freq * t)) / 1.5);
        }
        return x;
    }

    private static float[] Noise(double seconds, float amplitude, int seed = 1)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, (int)(seconds * Rate)).Select(_ => (float)((rng.NextDouble() * 2 - 1) * amplitude)).ToArray();
    }

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Selector_KeepsTheSpeech_DropsTheSilences()
    {
        var clip = Concat(Noise(2, 0.001f), Voice(4), Noise(3, 0.001f, 2), Voice(5), Noise(2, 0.001f, 3));
        var selected = SpeechSelector.Select([clip], Rate);
        Assert.InRange(selected.Seconds, 8.8, 9.6); // 9 s of voice (+ 100 ms kept around each part)
        Assert.Equal(0, selected.Clipped);
        Assert.True(selected.Speech.Take(3 * Rate).All(v => MathF.Abs(v) < 0.5f));
    }

    [Fact]
    public void Selector_PutsTheBest3SecondsFirst_AndAvoidsSaturation()
    {
        // A saturated (shouted) part first, then a clean one: the clean one must lead (it becomes the prompt).
        var shouted = Voice(4, 2.0f).Select(v => Math.Clamp(v, -1f, 1f)).ToArray();
        var clean = Voice(6, 0.3f, 200);
        var selected = SpeechSelector.Select([shouted, clean], Rate);
        var first = selected.Speech.Take(3 * Rate).ToArray();
        Assert.True(first.Max(MathF.Abs) < 0.9f, "the prompt contains the saturated part");
        Assert.True(selected.Clipped > 0.01);
        Assert.InRange(selected.Seconds, 9.5, 10.5);
    }

    [Fact]
    public void Selector_SilenceAlone_GivesNothing_AndOneMinuteIsTheMost()
    {
        Assert.Equal(0, SpeechSelector.Select([Noise(5, 0.0005f)], Rate).Seconds);
        var long90 = SpeechSelector.Select([Voice(90)], Rate);
        Assert.Equal(SpeechSelector.MaximumSeconds, long90.Seconds, 1);
    }

    [Fact]
    public void Library_AddsRenamesDeletes_AndSurvivesADamagedIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "spoka_myvoices_" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new CustomVoiceLibrary(root);
            Assert.Empty(lib.Load());
            var imprint = Path.GetTempFileName();
            File.WriteAllBytes(imprint, [1, 2, 3]);
            var v = lib.Add("  Capitaine   Rex  ", imprint, 23.44);
            Assert.False(File.Exists(imprint));                 // moved into the library
            Assert.True(File.Exists(lib.PathOf(v)));
            Assert.Equal("Capitaine Rex", v.Name);
            Assert.Equal(23.4, v.SpeechSeconds);
            Assert.StartsWith(CustomVoice.KeyPrefix, v.Key);

            lib.Rename(v, "");
            Assert.Equal("Ma voix", Assert.Single(lib.Load()).Name);

            // A voice whose file is gone is not listed; a damaged index lists nothing, without error.
            File.Delete(lib.PathOf(v));
            Assert.Empty(lib.Load());
            File.WriteAllText(Path.Combine(lib.Directory, "voices.json"), "{ pas du json");
            Assert.Empty(lib.Load());

            var imprint2 = Path.GetTempFileName();
            var w = lib.Add(new string('x', 80), imprint2, 10);
            Assert.Equal(40, w.Name.Length);
            lib.Delete(w);
            Assert.Empty(lib.Load());
            Assert.False(File.Exists(lib.PathOf(w)));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Settings_KeepCustomVoices_AndTheMode()
    {
        var s = new AppSettings();
        s.VoiceChanger.VoiceKey = "custom:abc123";
        s.VoiceChanger.Mode = VoiceChangerMode.Stable;
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal("custom:abc123", back.VoiceChanger.VoiceKey);
        Assert.Equal(VoiceChangerMode.Stable, back.VoiceChanger.Mode);

        var old = SettingsStore.Deserialize("""{ "VoiceChanger": { "VoiceKey": "F2" } }""");
        Assert.Equal(VoiceChangerMode.Reactive, old.VoiceChanger.Mode);   // 2.15 settings: the new default
        Assert.Equal(VoiceChangerMode.Reactive, SettingsStore.Deserialize("""{ "VoiceChanger": { "Mode": 7 } }""").VoiceChanger.Mode);
    }

    [Fact]
    public void Profiles_MatchWhatWasMeasured()
    {
        var r = VoiceChangerProfile.For(VoiceChangerMode.Reactive);
        Assert.Equal((200, 3, 1.5, 2.5, 1.0), (r.BlockMs, r.Steps, r.PromptSeconds, r.ContentContextSeconds, r.DitContextSeconds));
        var s = VoiceChangerProfile.For(VoiceChangerMode.Stable);
        Assert.Equal((300, 4, 2.0, 2.5, 0.5), (s.BlockMs, s.Steps, s.PromptSeconds, s.ContentContextSeconds, s.DitContextSeconds));
        Assert.True(r.DitContextSeconds <= r.ContentContextSeconds && s.DitContextSeconds <= s.ContentContextSeconds);
        // Under a busy GPU the reactive mode falls back to the 2.16 contexts (measured without gaps under a game load).
        Assert.Equal((1.5, 0.5), (r.LightContentContextSeconds, r.LightDitContextSeconds));
        Assert.Null(s.LightContentContextSeconds);
        Assert.True(r.BlockMs + r.MarginMs < s.BlockMs + s.MarginMs);
    }

    [Fact]
    public void CreatorPack_IsPinned()
    {
        var pack = VoiceChangerCatalog.Creator;
        Assert.Equal(["fbank.bin", "melbasis.bin", "campplus.onnx"], pack.Files.Select(f => f.Name));
        Assert.All(pack.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        Assert.All(pack.Files, f => Assert.StartsWith("https://github.com/Spokator/SpokaSoundBoard/releases/download/voice-models-1/", pack.UrlOf(f).AbsoluteUri));
        Assert.NotEqual(pack.Id, VoiceChangerCatalog.SeedVc.Id); // 2.15 installs keep their models
    }

    [Fact]
    public void FullBackup_KeepsMyVoices()
    {
        var data = Path.Combine(Path.GetTempPath(), "spoka_bk_" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(Path.GetTempPath(), "spoka_bk2_" + Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(Path.GetTempPath(), "spoka_bk_" + Guid.NewGuid().ToString("N") + ".spokabackup");
        try
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "settings.json"), SettingsStore.Serialize(new AppSettings()));
            var lib = new CustomVoiceLibrary(Path.Combine(data, VoiceModelStore.FolderName));
            var imprint = Path.GetTempFileName();
            File.WriteAllBytes(imprint, Enumerable.Repeat((byte)7, 1000).ToArray());
            var voice = lib.Add("Pirate", imprint, 30);
            Directory.CreateDirectory(Path.Combine(lib.Directory, "_brouillon"));
            File.WriteAllText(Path.Combine(lib.Directory, "_brouillon", "x.wav"), "draft");

            var manifest = FullBackup.Create(data, zip, includeMusic: false, "2.16.0", "Installeur");
            Assert.Equal(1, manifest.CustomVoices);

            FullBackup.Restore(zip, other);
            var restored = new CustomVoiceLibrary(Path.Combine(other, VoiceModelStore.FolderName));
            var back = Assert.Single(restored.Load());
            Assert.Equal("Pirate", back.Name);
            Assert.Equal(1000, new FileInfo(restored.PathOf(back)).Length);
            Assert.False(Directory.Exists(Path.Combine(restored.Directory, "_brouillon")));
            Assert.Equal(voice.Id, back.Id);
        }
        finally
        {
            foreach (var d in new[] { data, other }) try { Directory.Delete(d, true); } catch { /* best effort */ }
            try { File.Delete(zip); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A real voice made by the real engine from a real recording, then used by the real session: opt-in with
    /// SPOKA_VOICE_ENGINE, SPOKA_VC_MODELS, SPOKA_VC_CREATOR (folder of the creation models) and SPOKA_VC_SPEECH.
    /// </summary>
    [Fact]
    public async Task RealEngine_MakesAVoice_ThatTheSessionCanUse()
    {
        var engine = Environment.GetEnvironmentVariable("SPOKA_VOICE_ENGINE");
        var models = Environment.GetEnvironmentVariable("SPOKA_VC_MODELS");
        var creator = Environment.GetEnvironmentVariable("SPOKA_VC_CREATOR");
        var speechFile = Environment.GetEnvironmentVariable("SPOKA_VC_SPEECH");
        if (new[] { engine, models, creator, speechFile }.Any(string.IsNullOrEmpty)) return;

        var speech = SpeechSelector.Select(VoiceImprintMaker.Decode([speechFile!]), Rate);
        Assert.True(speech.Seconds > 10);
        var imprint = await VoiceImprintMaker.BuildAsync(engine!, models!, creator!, speech);
        try
        {
            var bytes = await File.ReadAllBytesAsync(imprint);
            Assert.Equal("SVCV"u8.ToArray(), bytes[..4]);
            Assert.Equal(258, BitConverter.ToInt32(bytes, 8)); // 3 s of prompt at 22.05 kHz / 256
            Assert.Equal(12 + 4 * (192 + 80 * 258 + 384 * 258), bytes.Length);

            using var session = new VoiceChangerSession(engine!, models!);
            var ready = await session.StartAsync(imprint, VoiceChangerProfile.Reactive);
            Assert.InRange(ready.BlockMs, 200, 600);
            session.Stop();
        }
        finally
        {
            File.Delete(imprint);
        }
    }
}
