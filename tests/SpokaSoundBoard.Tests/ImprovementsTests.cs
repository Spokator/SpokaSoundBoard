using System.IO.Compression;
using System.Text.Json;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

public class ImprovementsTests
{
    [Fact]
    public void Loudness_BringsQuietAndLoudFilesToTheSameLevel()
    {
        var quiet = RoutingTests.WriteToneScaled(3, 0.05f);
        var loud = RoutingTests.WriteToneScaled(3, 0.7f);
        try
        {
            var gQuiet = LoudnessAnalyzer.AnalyzeFile(quiet);
            var gLoud = LoudnessAnalyzer.AnalyzeFile(loud);
            Assert.NotNull(gQuiet);
            Assert.NotNull(gLoud);
            Assert.True(gQuiet > 0, "un son faible doit être remonté");
            Assert.True(gLoud < 0, "un son fort doit être baissé");
            // 0.05 vs 0.7 is a 22.9 dB difference: the corrections must compensate it.
            Assert.InRange(gQuiet!.Value - gLoud!.Value, 21, 25);
        }
        finally
        {
            File.Delete(quiet);
            File.Delete(loud);
        }
    }

    [Fact]
    public void Loudness_IgnoresSilence()
    {
        Assert.Null(LoudnessAnalyzer.GainFor([1e-9, 1e-10]));
        Assert.Equal(12, LoudnessAnalyzer.GainFor([1e-5, 1e-5]));
    }

    [Fact]
    public void Pack_RoundTrip_KeepsSoundsAndPresets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spoka_pack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sound = RoutingTests.WriteTone(0.5f);
        try
        {
            var pad = new SoundPad { Name = "Alarme", FilePath = sound, Color = "#EF4444", Volume = 0.7f, Loop = true, StartSeconds = 0.1, NormalizationDb = -3 };
            pad.Hotkey.Set(0x70, HotkeyModifiers.None);
            var preset = new EffectPreset { Name = "Mon pilote", Settings = new VoiceEffectSettings { RadioEnabled = true, PitchSemitones = -2 } };
            var packPath = Path.Combine(dir, "test.spokapack");

            Assert.Equal(1, SoundboardPack.Export(packPath, "Pack de test", [pad], [preset]));
            var (pads, presets) = SoundboardPack.Import(packPath, Path.Combine(dir, "lib"));

            var imported = Assert.Single(pads);
            Assert.Equal("Alarme", imported.Name);
            Assert.True(File.Exists(imported.FilePath));
            Assert.Equal(0.7f, imported.Volume);
            Assert.True(imported.Loop);
            Assert.Equal(-3, imported.NormalizationDb);
            Assert.NotEqual(pad.Id, imported.Id);
            Assert.False(imported.Hotkey.IsSet);
            var p = Assert.Single(presets);
            Assert.True(p.Settings.RadioEnabled);
            Assert.False(p.IsBuiltIn);
        }
        finally
        {
            File.Delete(sound);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Pack_Import_RejectsPathTraversal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spoka_pack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var packPath = Path.Combine(dir, "evil.spokapack");
            using (var zip = ZipFile.Open(packPath, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("sounds/..\\..\\evil.mp3").Open())) w.Write("x");
                using var m = new StreamWriter(zip.CreateEntry("pack.json").Open());
                m.Write("""{"Version":1,"Name":"Evil","Pads":[{"Name":"x","File":"..\\..\\evil.mp3","Volume":1,"Color":"#fff","Loop":false,"StartSeconds":0,"EndSeconds":0}],"Presets":[]}""");
            }
            var lib = Path.Combine(dir, "lib");
            var (pads, _) = SoundboardPack.Import(packPath, lib);
            Assert.False(File.Exists(Path.Combine(dir, "evil.mp3")));
            Assert.All(pads, p => Assert.StartsWith(Path.GetFullPath(lib), p.FilePath));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("v1.5.0", "1.4.0", true)]
    [InlineData("v1.4.0", "1.4.0", false)]
    [InlineData("v1.3.9", "1.4.0", false)]
    public void UpdateChecker_ComparesVersions(string tag, string current, bool expected)
    {
        var json = $$"""
            {"tag_name":"{{tag}}","draft":false,"prerelease":false,"html_url":"https://github.com/x/y/releases/tag/{{tag}}","body":"notes",
             "assets":[{"name":"SpokaSoundBoard-Setup-1.5.0.exe","browser_download_url":"https://example.com/setup.exe"},
                       {"name":"SpokaSoundBoard.exe","browser_download_url":"https://example.com/app.exe"}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var update = UpdateChecker.Parse(doc.RootElement, Version.Parse(current));
        Assert.Equal(expected, update != null);
        if (update != null) Assert.Equal("https://example.com/setup.exe", update.DownloadUrl);
    }
}
