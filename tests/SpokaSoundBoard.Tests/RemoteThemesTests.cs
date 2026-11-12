using System.IO.Compression;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Remote;

namespace SpokaSoundBoard.Tests;

/// <summary>2.8: phone themes, embedded icons, an icon per sound.</summary>
public sealed class RemoteThemesTests
{
    [Fact]
    public void EveryIconThePageUsesIsEmbedded()
    {
        var html = RemotePage.Html;
        var used = new HashSet<string>();
        foreach (Match m in Regex.Matches(html, "#i-([a-z0-9-]+)\"")) used.Add(m.Groups[1].Value);
        var start = html.IndexOf("const ACTIONS = {", StringComparison.Ordinal);
        var actions = html[start..html.IndexOf("};", start, StringComparison.Ordinal)];
        foreach (Match m in Regex.Matches(actions, @": \['([a-z0-9-]+)'")) used.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(html, @"icon\('([a-z0-9-]+)'\)")) used.Add(m.Groups[1].Value);
        // Names chosen at run time (toggles, play state, edit mode…).
        used.UnionWith(["microphone-off", "headphones", "player-play", "player-pause", "eye", "eye-off", "pencil", "check", "x",
            "plus", "chevron-left", "chevron-right", "device-mobile", "wand", "message-circle", "wave-sine", "trophy", "star", "music"]);

        var missing = used.Where(n => !RemoteIcons.Names.Contains(n)).ToList();
        Assert.Empty(missing);
        Assert.True(used.Count > 30);
        foreach (var name in RemoteIcons.Names) Assert.Contains($"id=\"i-{name}\"", RemoteIcons.Sprite);
    }

    [Fact]
    public void EveryPadIconIsEmbeddedAndNamedInFrench()
    {
        foreach (var icon in PadIcons.All)
        {
            Assert.Contains(icon.Key, RemoteIcons.Names);
            Assert.False(string.IsNullOrWhiteSpace(icon.Label));
        }
        Assert.Equal(PadIcons.All.Count, PadIcons.All.Select(i => i.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("Applaudissements", "confetti")]
    [InlineData("Rire du public", "mood-happy")]
    [InlineData("MDR", "mood-happy")]
    [InlineData("Fou rire", "mood-crazy-happy")]
    [InlineData("Vine boom", "bomb")]
    [InlineData("Explosion nucléaire", "bomb")]
    [InlineData("Sirène de police", "alarm")]
    [InlineData("Corne de brume", "speakerphone")]
    [InlineData("Bip radio", "radio")]
    [InlineData("Roulement de tambour", "music")]
    [InlineData("Échec", "thumb-down")]
    [InlineData("Prout", "poo")]
    [InlineData("Miaou", "cat")]
    [InlineData("Suspense", "sparkles")]
    [InlineData("Enregistrement 3", "wave-sine")]
    [InlineData("", "wave-sine")]
    [InlineData(null, "wave-sine")]
    public void Icons_AreGuessedFromTheName(string? name, string expected) => Assert.Equal(expected, PadIcons.Guess(name));

    [Fact]
    public void Icons_ChosenBeatsGuessedAndUnknownIsIgnored()
    {
        Assert.Equal("cat", PadIcons.Resolve("cat", "Explosion"));
        Assert.Equal("bomb", PadIcons.Resolve("", "Explosion"));
        Assert.Equal("bomb", PadIcons.Resolve("licorne", "Explosion"));
        Assert.Equal("", PadIcons.Clean("licorne"));
        Assert.Equal("cat", PadIcons.Clean("cat"));
        // « chat » the animal, not « chatter »: whole word starts only.
        Assert.Equal("wave-sine", PadIcons.Guess("Chatterton"));
    }

    [Fact]
    public void Settings_KeepAValidIconOnly()
    {
        var s = SettingsStore.Deserialize("""{ "Pads": [ { "Name": "a", "Icon": "bomb" }, { "Name": "b", "Icon": "dragon" }, { "Name": "c" } ] }""");
        Assert.Equal(["bomb", "", ""], s.Pads.Select(p => p.Icon));
    }

    [Fact]
    public void Packs_CarryTheIcon()
    {
        var dir = Directory.CreateTempSubdirectory("spoka-pack28-");
        try
        {
            var wav = Path.Combine(dir.FullName, "boom.wav");
            using (var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
                w.WriteSamples(new float[4800], 0, 4800);
            var pack = Path.Combine(dir.FullName, "p.spokapack");
            SoundboardPack.Export(pack, "P", [new SoundPad { Name = "Boom", FilePath = wav, Icon = "rocket" }], []);
            var (pads, _) = SoundboardPack.Import(pack, Path.Combine(dir.FullName, "lib"));
            Assert.Equal("rocket", pads[0].Icon);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Page_OffersTheThreeThemes()
    {
        Assert.Equal(["nuit", "soleil", "ocean"], RemotePage.Themes);
        foreach (var theme in RemotePage.Themes)
        {
            Assert.Contains($"[data-theme={theme}]", RemotePage.Html);
            Assert.Contains($"['{theme}', '", RemotePage.Html);
        }
        Assert.Contains("Tabler", File.ReadAllText(Path.Combine(FindRepo(), "src", "SpokaSoundBoard.Core", "Remote", "RemoteIcons.cs")));
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SpokaSoundBoard.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
