using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Tests;

/// <summary>2.12: settings applied automatically while a game runs.</summary>
public sealed class GameProfileTests
{
    private sealed class FakeApp : IProfileTarget
    {
        public string Category { get; set; } = "";
        public bool Effects { get; set; } = true;
        public bool Radio { get; set; }
        public bool NoiseSuppression { get; set; }
        public bool MediaToMic { get; set; } = true;
        public string? Preset { get; set; } = "Voix normale";
        public float Pitch { get; set; }
        public List<string> Messages { get; } = [];
        public string[] Known { get; set; } = ["Voix normale", "Pilote d'hélico", "Robot"];

        public bool ApplyPreset(string name)
        {
            if (!Known.Contains(name)) return false;
            Preset = name;
            Pitch = name == "Robot" ? 5 : 0;
            return true;
        }

        public object CaptureVoice() => (Preset, Pitch);

        public void RestoreVoice(object snapshot)
        {
            var (preset, pitch) = ((string?, float))snapshot;
            Preset = preset;
            Pitch = pitch;
        }

        public void Notify(string message) => Messages.Add(message);
    }

    private static readonly DateTime T0 = new(2026, 11, 13, 20, 0, 0);
    private static HashSet<string> Running(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    private static GameProfile Cs2() => new()
    {
        Name = "Counter-Strike 2", ProcessName = "cs2", Preset = "Pilote d'hélico", Category = "Réactions",
        Radio = true, NoiseSuppression = true, MediaToMic = false,
    };

    [Theory]
    [InlineData("cs2.exe", "cs2")]
    [InlineData(@"C:\Program Files\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", "cs2")]
    [InlineData(" \"FortniteClient-Win64-Shipping.EXE\" ", "fortniteclient-win64-shipping")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ProgramNames_AreNormalized(string? input, string expected) => Assert.Equal(expected, GameProfile.NormalizeProcess(input));

    [Fact]
    public void TheGameStarts_ProfileApplied_TheGameEnds_SettingsBack()
    {
        var app = new FakeApp { Pitch = 2.5f, Preset = null }; // a voice tuned by hand
        var switcher = new GameProfileSwitcher(app);
        GameProfile[] profiles = [Cs2()];

        switcher.Update(profiles, Running("explorer", "discord"), "discord", T0);
        Assert.Null(switcher.Active);

        switcher.Update(profiles, Running("explorer", "discord", "cs2"), "discord", T0.AddSeconds(2));
        Assert.Same(profiles[0], switcher.Active);
        Assert.Equal("Pilote d'hélico", app.Preset);
        Assert.Equal("Réactions", app.Category);
        Assert.True(app.Radio);
        Assert.True(app.NoiseSuppression);
        Assert.False(app.MediaToMic);
        Assert.Contains("Pilote d'hélico", app.Messages[^1]);

        // Closed, then the leave delay.
        switcher.Update(profiles, Running("explorer", "discord"), "discord", T0.AddSeconds(60));
        Assert.NotNull(switcher.Active);
        switcher.Update(profiles, Running("explorer", "discord"), "discord", T0.AddSeconds(66));
        Assert.Null(switcher.Active);
        Assert.Null(app.Preset);
        Assert.Equal(2.5f, app.Pitch); // the hand-tuned voice is back, not just a preset name
        Assert.Equal("", app.Category);
        Assert.False(app.Radio);
        Assert.False(app.NoiseSuppression);
        Assert.True(app.MediaToMic);
        Assert.StartsWith("Fin du profil", app.Messages[^1]);
    }

    [Fact]
    public void WhatTheUserChangesDuringTheGame_Stays()
    {
        var app = new FakeApp();
        var switcher = new GameProfileSwitcher(app);
        GameProfile[] profiles = [Cs2()];
        switcher.Update(profiles, Running("cs2"), "cs2", T0);

        app.ApplyPreset("Robot");   // picked by hand in game
        app.Radio = false;          // turned off by hand

        switcher.Update(profiles, Running(), null, T0.AddSeconds(10));
        switcher.Update(profiles, Running(), null, T0.AddSeconds(20));
        Assert.Null(switcher.Active);
        Assert.Equal("Robot", app.Preset);
        Assert.False(app.Radio);
        Assert.Equal("", app.Category);     // untouched by the user: restored
        Assert.True(app.MediaToMic);
    }

    [Fact]
    public void AQuickAltTabOrRestart_DoesNotFlicker()
    {
        var app = new FakeApp();
        var switcher = new GameProfileSwitcher(app);
        GameProfile[] profiles = [Cs2()];
        switcher.Update(profiles, Running("cs2"), "cs2", T0);
        var messages = app.Messages.Count;

        switcher.Update(profiles, Running(), null, T0.AddSeconds(1));       // crashed…
        switcher.Update(profiles, Running("cs2"), "cs2", T0.AddSeconds(4)); // …and back within the delay
        switcher.Update(profiles, Running("cs2"), "cs2", T0.AddSeconds(30));
        Assert.NotNull(switcher.Active);
        Assert.Equal(messages, app.Messages.Count);
        Assert.Equal("Pilote d'hélico", app.Preset);
    }

    [Fact]
    public void ForegroundOnlyProfiles_AndPriorities()
    {
        var discordOnly = new GameProfile { Name = "Valorant", ProcessName = "valorant", Radio = true, OnlyWhenForeground = true };
        var cs2 = Cs2();
        GameProfile[] profiles = [discordOnly, cs2];

        // Valorant runs behind: its foreground-only profile does not count; CS2 runs: it wins.
        Assert.Same(cs2, GameProfileSwitcher.Choose(profiles, Running("valorant", "cs2"), "discord"));
        // Valorant in front: it wins over the first in the list.
        Assert.Same(discordOnly, GameProfileSwitcher.Choose(profiles, Running("valorant", "cs2"), "valorant"));
        // Disabled or empty profiles are ignored.
        cs2.Enabled = false;
        Assert.Null(GameProfileSwitcher.Choose(profiles, Running("valorant", "cs2"), "discord"));
        Assert.Null(GameProfileSwitcher.Choose([new GameProfile { Name = "Vide", ProcessName = "cs2" }], Running("cs2"), "cs2"));
    }

    [Fact]
    public void SwitchingGames_RestoresTheFirstBeforeApplyingTheSecond()
    {
        var app = new FakeApp();
        var switcher = new GameProfileSwitcher(app);
        var minecraft = new GameProfile { Name = "Minecraft", ProcessName = "javaw", Category = "Ambiance", Effects = false };
        var cs2 = Cs2();
        GameProfile[] profiles = [cs2, minecraft];

        switcher.Update(profiles, Running("cs2"), "cs2", T0);
        switcher.Update(profiles, Running("javaw"), "javaw", T0.AddSeconds(30));
        Assert.Same(minecraft, switcher.Active);
        Assert.Equal("Ambiance", app.Category);
        Assert.False(app.Effects);
        Assert.Equal("Voix normale", app.Preset); // CS2's effect was put back first
        Assert.False(app.Radio);

        switcher.Stop();
        Assert.Null(switcher.Active);
        Assert.True(app.Effects);
        Assert.Equal("", app.Category);
    }

    [Fact]
    public void ADeletedPreset_IsSkipped_NotAnError()
    {
        var app = new FakeApp { Known = ["Voix normale"] };
        var switcher = new GameProfileSwitcher(app);
        switcher.Update([Cs2()], Running("cs2"), null, T0);
        Assert.Equal("Voix normale", app.Preset);
        Assert.True(app.Radio);
        Assert.DoesNotContain("effet", app.Messages[^1]);
    }

    [Fact]
    public void Settings_KeepProfiles_AndCleanThem()
    {
        var s = SettingsStore.Deserialize("""
            { "GameProfiles": [ { "Name": "  ", "ProcessName": "C:\\Jeux\\CS2.exe", "Radio": true, "Preset": "" }, null ] }
            """);
        var p = Assert.Single(s.GameProfiles);
        Assert.Equal("cs2", p.ProcessName);
        Assert.Equal("cs2", p.Name);
        Assert.Null(p.Preset);
        Assert.True(p.Radio);
        Assert.Null(p.Effects);
        Assert.Empty(SettingsStore.Deserialize("{}").GameProfiles);
    }
}
