using System.IO.Compression;
using NAudio.Wave;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Tests;

/// <summary>2.3: tabs, play options, headphones-only sounds, hotkey conflicts, microphone recording.</summary>
public sealed class SoundboardOrganisationTests
{
    private const int Rate = MixerCore.SampleRate;

    private static SoundPad Pad(string name, string category = "") => new() { Name = name, Category = category };

    [Fact]
    public void Categories_FollowThePads()
    {
        var pads = new List<SoundPad> { Pad("a", "Mèmes"), Pad("b"), Pad("c", "Bruitages"), Pad("d", "Mèmes") };
        var categories = new List<string> { "Musique", "" };

        Assert.True(PadCategories.Merge(categories, pads));
        Assert.Equal(["Musique", "Mèmes", "Bruitages"], categories);
        Assert.False(PadCategories.Merge(categories, pads));

        // « mèmes » typed in lower case joins the existing tab.
        Assert.Equal("Mèmes", PadCategories.Canonical(categories, "  mèmes "));

        PadCategories.Rename(categories, pads, "Mèmes", "Mèmes drôles");
        Assert.Equal(["Musique", "Mèmes drôles", "Bruitages"], categories);
        Assert.Equal("Mèmes drôles", pads[3].Category);

        // Renaming onto an existing tab merges both.
        PadCategories.Rename(categories, pads, "Bruitages", "musique");
        Assert.Equal(["Musique", "Mèmes drôles"], categories);
        Assert.Equal("Musique", pads[2].Category);

        PadCategories.Remove(categories, pads, "Mèmes drôles");
        Assert.Equal(["Musique"], categories);
        Assert.Equal("", pads[0].Category);
        Assert.Equal(4, pads.Count); // the sounds stay

        Assert.Equal(PadCategories.MaxLength, PadCategories.Clean(new string('x', 80)).Length);
    }

    [Fact]
    public void Settings_KeepCategoriesAndPlayOptions()
    {
        var s = new AppSettings();
        s.Pads.Add(new SoundPad { Name = "Klaxon", Category = "Bruitages", PlayMode = PadPlayMode.Overlap, StopOthers = true, HeadphonesOnly = true });
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal(["Bruitages"], back.PadCategories);
        var pad = back.Pads[0];
        Assert.Equal(("Bruitages", PadPlayMode.Overlap, true, true), (pad.Category, pad.PlayMode, pad.StopOthers, pad.HeadphonesOnly));

        // Files from 2.2 have none of these fields.
        var old = SettingsStore.Deserialize("""{ "Pads": [ { "Name": "x", "PlayMode": 7 } ] }""");
        Assert.Equal(("", PadPlayMode.Toggle, false), (old.Pads[0].Category, old.Pads[0].PlayMode, old.Pads[0].HeadphonesOnly));
        Assert.Empty(old.PadCategories);
    }

    [Fact]
    public void Hotkeys_ConflictsAreFound()
    {
        var f1 = new HotkeyBinding();
        f1.Set(0x70, HotkeyModifiers.None);
        var alsoF1 = new HotkeyBinding();
        alsoF1.Set(0x70, HotkeyModifiers.None);
        var shiftF1 = new HotkeyBinding();
        shiftF1.Set(0x70, HotkeyModifiers.Shift);
        (HotkeyBinding, string)[] all = [(f1, "Applaudissements"), (alsoF1, "Klaxon"), (shiftF1, "Sirène"), (new HotkeyBinding(), "Vide")];

        Assert.Equal(["Klaxon"], HotkeyMatching.Conflicts(f1, all));
        Assert.Empty(HotkeyMatching.Conflicts(shiftF1, all));
        Assert.Empty(HotkeyMatching.Conflicts(new HotkeyBinding(), all));
    }

    [Fact]
    public void Packs_CarryCategoriesAndOldPacksGetOneNamedAfterThem()
    {
        var dir = Directory.CreateTempSubdirectory("spoka-pack23-");
        try
        {
            var wav = Path.Combine(dir.FullName, "boom.wav");
            using (var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)))
                w.WriteSamples(new float[Rate / 10], 0, Rate / 10);

            var pack = Path.Combine(dir.FullName, "a.spokapack");
            SoundboardPack.Export(pack, "Mes mèmes", [new SoundPad { Name = "Boom", FilePath = wav, Category = "Explosions", PlayMode = PadPlayMode.Restart }], []);
            var (pads, _) = SoundboardPack.Import(pack, Path.Combine(dir.FullName, "lib"));
            Assert.Equal(("Explosions", PadPlayMode.Restart), (pads[0].Category, pads[0].PlayMode));

            // A version 1 pack (2.2 and earlier): no category, so the sounds land in a tab named after the pack.
            var oldPack = Path.Combine(dir.FullName, "old.spokapack");
            using (var zip = ZipFile.Open(oldPack, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(wav, "sounds/boom.wav");
                using var writer = new StreamWriter(zip.CreateEntry("pack.json").Open());
                writer.Write("""{"Version":1,"Name":"Pack de Léo","Pads":[{"Name":"Boom","File":"boom.wav","Volume":1,"Color":"#7C5CFF","Loop":false,"StartSeconds":0,"EndSeconds":0,"SourceUrl":null,"NormalizationDb":null}],"Presets":[]}""");
            }
            var (oldPads, _) = SoundboardPack.Import(oldPack, Path.Combine(dir.FullName, "lib"));
            Assert.Equal("Pack de Léo", oldPads[0].Category);
            Assert.Equal(PadPlayMode.Toggle, oldPads[0].PlayMode);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static float Peak(ReadOnlySpan<float> x)
    {
        var p = 0f;
        foreach (var v in x) p = MathF.Max(p, MathF.Abs(v));
        return p;
    }

    [Fact]
    public async Task HeadphonesOnlySounds_NeverReachTheMicrophone()
    {
        var dir = Directory.CreateTempSubdirectory("spoka-private-");
        try
        {
            var wav = Path.Combine(dir.FullName, "tone.wav");
            using (var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)))
                for (var i = 0; i < Rate; i++) { w.WriteSample(0.5f); w.WriteSample(0.5f); }

            var mix = new MixSettings { MediaToMicVolume = 1f, GameSounds = true, DiscordSounds = true };
            using var music = new MusicPlayer(Rate);
            using var pads = new SoundPadPlayer(Rate);
            using var privatePads = new SoundPadPlayer(Rate);
            var core = new MixerCore(new VoiceEffectSettings(), mix, music, pads, null, privatePads);
            await privatePads.PlayAsync(Guid.NewGuid(), wav, 1f, loop: false);

            const int frames = 4800;
            var game = new float[frames * 2];
            var discord = new float[frames * 2];
            var monitor = new float[frames * 2];
            core.Render(game, discord, monitor, frames);

            Assert.Equal(0f, Peak(game));
            Assert.Equal(0f, Peak(discord));
            Assert.True(Peak(monitor) > 0.1f);

            // Without headphones the sound still runs to its end instead of waiting forever.
            for (var i = 0; i < 12; i++) core.Render(game, discord, Span<float>.Empty, frames);
            Assert.Equal(0, privatePads.ActiveCount);
        }
        finally
        {
            await Task.Delay(200);
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Recorder_KeepsTheVoiceAndTrimsTheSilence()
    {
        var dir = Directory.CreateTempSubdirectory("spoka-record-");
        try
        {
            var recorder = new VoiceRecorder(Rate);
            var silence = new float[Rate / 2];
            var voice = new float[Rate / 2];
            for (var i = 0; i < voice.Length; i++) voice[i] = 0.4f * MathF.Sin(2 * MathF.PI * 220 * i / Rate);

            // As the audio thread would: 10 ms blocks.
            void Feed(float[] samples)
            {
                for (var i = 0; i < samples.Length; i += 480) recorder.Write(samples.AsSpan(i, Math.Min(480, samples.Length - i)));
            }
            Feed(silence);
            await Task.Delay(100);
            Feed(voice);
            await Task.Delay(100);
            Feed(silence);

            var path = Path.Combine(dir.FullName, "rec.wav");
            var length = await recorder.StopAsync(path);
            Assert.InRange(length.TotalSeconds, 0.5, 0.65); // the voice plus 50 ms margins
            using var reader = new AudioFileReader(path);
            Assert.Equal(Rate, reader.WaveFormat.SampleRate);
            Assert.InRange(reader.TotalTime.TotalSeconds, 0.5, 0.65);

            var empty = new VoiceRecorder(Rate);
            Feed2(empty, silence);
            Assert.Equal(TimeSpan.Zero, await empty.StopAsync(Path.Combine(dir.FullName, "empty.wav")));
            Assert.False(File.Exists(Path.Combine(dir.FullName, "empty.wav")));
        }
        finally
        {
            dir.Delete(true);
        }

        static void Feed2(VoiceRecorder r, float[] samples) => r.Write(samples);
    }

    [Fact]
    public void Recorder_TrimFindsTheSound()
    {
        var x = new float[1000];
        x[400] = 0.5f;
        x[600] = -0.5f;
        Assert.Equal((0, 1000), VoiceRecorder.TrimSilence(x, x.Length, 20000)); // 1000-sample margins cover everything
        Assert.Equal((200, 801), VoiceRecorder.TrimSilence(x, x.Length, 4000));
        Assert.Equal((350, 651), VoiceRecorder.TrimSilence(x, x.Length, 1000));
        Assert.Equal((0, 0), VoiceRecorder.TrimSilence(new float[10], 10, 1000));
    }
}
